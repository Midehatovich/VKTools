using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using VKTools.Core.Logging;
using VKTools.Core.Progress;
using VKTools.Revit.Infrastructure;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace VKTools.Revit.UI
{
    /// <summary>
    /// Окно прогресса с кнопкой «Отменить» на время долгой операции в контексте Revit API.
    /// Revit однопоточный: операция идёт в главном потоке, поэтому окно перерисовывается и принимает
    /// нажатие «Отменить» только когда операция отдаёт управление — в <see cref="Report"/> и в событии
    /// Revit ProgressChanged (долгие внутренние операции Revit). На это время главное окно Revit
    /// отключено, чтобы в нём нельзя было ничего нажать (docs/ARCHITECTURE.md, D-016).
    /// </summary>
    internal sealed class ProgressScope : IOperationProgress, IDisposable
    {
        private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(150);

        private readonly UIApplication _app;
        private readonly IntPtr _revitWindow;
        private readonly Window? _ownerWindow;
        private readonly Window _window;
        private readonly TextBlock _stage;
        private readonly TextBlock _revitStage;
        private readonly TextBlock _elapsed;
        private readonly ProgressBar _bar;
        private readonly Button _cancel;
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private TimeSpan _lastPump = TimeSpan.MinValue;
        private string _revitCaption = string.Empty;
        private TimeSpan _revitStarted;
        private TimeSpan _revitLogged = TimeSpan.MinValue;
        private bool _revitDisabled;
        private volatile bool _disposed;

        // Сторож: если главный поток долго не отдаёт управление (вызов Revit не возвращается), пишет об этом в лог
        // из фонового потока — по логу видно, сколько ждали и на каком шаге, даже если Revit потом закрыли.
        private static readonly TimeSpan WatchdogQuiet = TimeSpan.FromSeconds(60);
        private readonly Timer _watchdog;
        private long _lastActivityTicks = DateTime.UtcNow.Ticks;
        private long _lastWatchdogLogTicks;
        private volatile string _currentStage = "Начинаю…";

        public ProgressScope(UIApplication app, string title, Window? ownerWindow = null)
        {
            _app = app;
            _revitWindow = app.MainWindowHandle;
            _ownerWindow = ownerWindow;

            _window = new Window
            {
                Title = "VK Tools — " + title,
                Width = 560,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true,
            };
            _window.ApplyDefaultLook();
            var panel = new StackPanel { Margin = new Thickness(14) };
            _stage = new TextBlock { Text = "Начинаю…", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, MinHeight = 34 };
            _bar = new ProgressBar { Height = 16, Margin = new Thickness(0, 8, 0, 6), IsIndeterminate = true, Minimum = 0, Maximum = 1 };
            _revitStage = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
            _elapsed = new TextBlock { Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 0) };
            _cancel = new Button { Content = "Отменить", Padding = new Thickness(14, 3, 14, 3), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            _cancel.Click += (s, e) => RequestCancel();
            panel.Children.Add(_stage);
            panel.Children.Add(_bar);
            panel.Children.Add(_revitStage);
            panel.Children.Add(_elapsed);
            panel.Children.Add(_cancel);
            _window.Content = panel;
            _watchdog = new Timer(OnWatchdog, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
            _window.Closing += (s, e) =>
            {
                // Крестик = «Отменить»: окно закроется само, когда операция остановится
                if (!_disposed)
                {
                    e.Cancel = true;
                    RequestCancel();
                }
            };

            try
            {
                if (_ownerWindow != null) _ownerWindow.IsEnabled = false;
                if (_revitWindow != IntPtr.Zero)
                {
                    new WindowInteropHelper(_window).Owner = _revitWindow;
                    NativeMethods.EnableWindow(_revitWindow, false);
                    _revitDisabled = true;
                }
                _window.Show();
                _app.Application.ProgressChanged += OnRevitProgress;
                _app.DialogBoxShowing += OnRevitDialog;
                Pump(force: true);
            }
            catch
            {
                // Окно прогресса не открылось — Revit не должен остаться отключённым
                Dispose();
                throw;
            }
        }

        public bool IsCancellationRequested { get; private set; }

        public void Report(string stage, int done = 0, int total = 0)
        {
            if (_disposed) return;
            bool changed = stage != _currentStage;
            _currentStage = stage;
            Touch();
            _stage.Text = stage;
            if (total > 0)
            {
                _bar.IsIndeterminate = false;
                _bar.Maximum = total;
                _bar.Value = Math.Min(done, total);
            }
            else
            {
                _bar.IsIndeterminate = true;
            }
            _revitStage.Text = string.Empty;
            // Новый шаг показываем сразу: следующим может быть долгий вызов Revit, во время которого окно не перерисуется
            Pump(force: changed);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _watchdog.Dispose();
            try
            {
                _app.Application.ProgressChanged -= OnRevitProgress;
                _app.DialogBoxShowing -= OnRevitDialog;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Revit закрывается — отписываться уже не от чего
            }
            if (_revitDisabled) NativeMethods.EnableWindow(_revitWindow, true);
            if (_ownerWindow != null) _ownerWindow.IsEnabled = true;
            _window.Close();
        }

        private void RequestCancel()
        {
            if (IsCancellationRequested) return;
            IsCancellationRequested = true;
            _cancel.IsEnabled = false;
            _cancel.Content = "Отменяю…";
            _stage.Text = "Отмена: останавливаюсь на ближайшем шаге и откатываю изменения…";
            Host.Log.Info("Пользователь нажал «Отменить».");
        }

        /// <summary>Долгая внутренняя операция Revit (пересчёт, фиксация): показываем её и даём отменить, если Revit позволяет.</summary>
        private void OnRevitProgress(object? sender, ProgressChangedEventArgs e)
        {
            Touch();
            try
            {
                if (IsCancellationRequested && e.Cancellable && (e.Stage == ProgressStage.PositionChanged || e.Stage == ProgressStage.Unchanged))
                {
                    e.Cancel();
                    return;
                }
                TraceRevit(e);
                if (e.Stage == ProgressStage.Finished)
                {
                    _revitStage.Text = string.Empty;
                }
                else if (!string.IsNullOrEmpty(e.Caption))
                {
                    _revitStage.Text = e.UpperRange > 0 ? $"Revit: {e.Caption} ({e.Position} из {e.UpperRange})" : "Revit: " + e.Caption;
                }
                Pump(force: false);
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is InvalidOperationException)
            {
                // Прогресс — не повод ронять операцию
            }
        }

        private void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

        /// <summary>Фоновый поток: только лог (FileLog потокобезопасен), ни Revit API, ни окно отсюда не трогаем.</summary>
        private void OnWatchdog(object? state)
        {
            if (_disposed) return;
            DateTime now = DateTime.UtcNow;
            TimeSpan quiet = now - new DateTime(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);
            TimeSpan sinceLog = now - new DateTime(Interlocked.Read(ref _lastWatchdogLogTicks), DateTimeKind.Utc);
            if (quiet < WatchdogQuiet || sinceLog < WatchdogQuiet) return;
            Interlocked.Exchange(ref _lastWatchdogLogTicks, now.Ticks);
            Host.Log.Info($"  … Revit не отвечает {OperationTimings.Seconds(quiet)}, шаг: {_currentStage}");
        }

        /// <summary>
        /// Revit открывает своё окно (предупреждение, запрос) посреди операции: в лог, и окно прогресса перестаёт
        /// быть поверх всех — иначе окно Revit может оказаться под ним, и операция выглядит зависшей.
        /// </summary>
        private void OnRevitDialog(object? sender, DialogBoxShowingEventArgs e)
        {
            try
            {
                string message = e is TaskDialogShowingEventArgs task ? task.Message
                    : e is MessageBoxShowingEventArgs box ? box.Message
                    : string.Empty;
                Host.Log.Warn($"  Revit показывает окно «{e.DialogId}»{(string.IsNullOrEmpty(message) ? string.Empty : ": " + message.Replace(Environment.NewLine, " "))}");
                _window.Topmost = false;
                _revitStage.Text = "Revit открыл своё окно — ответьте в нём (оно может быть за этим окном).";
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is InvalidOperationException)
            {
                // Только наблюдаем; окно Revit покажется в любом случае
            }
        }

        /// <summary>
        /// Внутренние операции Revit — в лог: начало (не чаще раза в 2 с для одной и той же), каждые 30 с
        /// положение, конец — если дольше секунды. Если Revit закрыли принудительно, видно, в чём он завис.
        /// </summary>
        private void TraceRevit(ProgressChangedEventArgs e)
        {
            TimeSpan now = _watch.Elapsed;
            string caption = e.Caption ?? string.Empty;
            if (e.Stage == ProgressStage.Started)
            {
                bool repeat = caption == _revitCaption && now - _revitLogged < TimeSpan.FromSeconds(2);
                _revitCaption = caption;
                _revitStarted = now;
                if (!repeat)
                {
                    _revitLogged = now;
                    Host.Log.Info($"  Revit → «{caption}»");
                }
            }
            else if (e.Stage == ProgressStage.Finished)
            {
                TimeSpan took = now - _revitStarted;
                if (took >= TimeSpan.FromSeconds(1)) Host.Log.Info($"  Revit ← «{_revitCaption}»: {OperationTimings.Seconds(took)}");
            }
            else if (now - _revitLogged >= TimeSpan.FromSeconds(30))
            {
                _revitLogged = now;
                string position = e.UpperRange > 0 ? $" {e.Position} из {e.UpperRange}" : string.Empty;
                Host.Log.Info($"  Revit … «{(caption.Length > 0 ? caption : _revitCaption)}»{position}, идёт {OperationTimings.Seconds(now - _revitStarted)}");
            }
        }

        /// <summary>Дать окну перерисоваться и принять нажатие «Отменить». Не чаще раза в 150 мс.</summary>
        private void Pump(bool force)
        {
            TimeSpan now = _watch.Elapsed;
            if (!force && now - _lastPump < PumpInterval) return;
            _lastPump = now;
            _elapsed.Text = "Прошло: " + OperationTimings.Seconds(now);
            try
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                    new DispatcherOperationCallback(f =>
                    {
                        ((DispatcherFrame)f).Continue = false;
                        return null;
                    }), frame);
                Dispatcher.PushFrame(frame);
            }
            catch (InvalidOperationException)
            {
                // Диспетчер сейчас нельзя прокачать (идёт разметка окна) — перерисуем на следующем шаге
            }
        }
    }

    internal static class NativeMethods
    {
        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnableWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool enable);
    }
}
