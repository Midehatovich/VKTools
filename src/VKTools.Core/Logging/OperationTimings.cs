using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace VKTools.Core.Logging
{
    /// <summary>
    /// Сколько времени заняли этапы операции. Одна строка в лог на запуск: по ней видно,
    /// что тормозит — анализ, удаление, Regenerate или фиксация (там же работают апдейтеры других надстроек).
    /// </summary>
    public sealed class OperationTimings
    {
        private readonly List<string> _order = new List<string>();
        private readonly Dictionary<string, TimeSpan> _totals = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _counts = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>
        /// Строка в лог в начале («→ этап») и в конце («← этап: 1,2 с») каждого замера. Если Revit закрыли
        /// принудительно, последняя «→» в логе показывает, на каком этапе он встал.
        /// </summary>
        public Action<string>? Trace { get; set; }

        public IDisposable Measure(string stage) => new Scope(this, stage);

        public void Add(string stage, TimeSpan elapsed)
        {
            if (!_totals.ContainsKey(stage))
            {
                _order.Add(stage);
                _totals[stage] = TimeSpan.Zero;
                _counts[stage] = 0;
            }
            _totals[stage] += elapsed;
            _counts[stage]++;
        }

        public TimeSpan Total(string stage) => _totals.TryGetValue(stage, out TimeSpan t) ? t : TimeSpan.Zero;

        /// <summary>«анализ 12,3 с; удаление ×4 0,4 с» — в порядке первого появления.</summary>
        public string Summary()
        {
            if (_order.Count == 0) return "—";
            return string.Join("; ", _order.Select(stage =>
            {
                int count = _counts[stage];
                string times = count > 1 ? $" ×{count}" : string.Empty;
                return $"{stage}{times} {Seconds(_totals[stage])}";
            }));
        }

        public static string Seconds(TimeSpan span) =>
            span.TotalSeconds.ToString(span.TotalSeconds < 10 ? "0.0" : "0", CultureInfo.GetCultureInfo("ru-RU")) + " с";

        private sealed class Scope : IDisposable
        {
            private readonly OperationTimings _owner;
            private readonly string _stage;
            private readonly Stopwatch _watch = Stopwatch.StartNew();
            private bool _done;

            public Scope(OperationTimings owner, string stage)
            {
                _owner = owner;
                _stage = stage;
                _owner.Trace?.Invoke("→ " + stage);
            }

            public void Dispose()
            {
                if (_done) return;
                _done = true;
                TimeSpan elapsed = _watch.Elapsed;
                _owner.Add(_stage, elapsed);
                _owner.Trace?.Invoke($"← {_stage}: {Seconds(elapsed)}");
            }
        }
    }
}
