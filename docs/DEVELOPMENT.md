# Разработка

## Что нужно

- .NET SDK 8.0.100 или новее в линейке 8.0 (`global.json`). В CI — 8.0.424.
- Для MSI — Windows (WiX собирает MSI только на Windows). Надстройка и тесты собираются на любой ОС.
- Revit не нужен для сборки: ссылочные сборки API приходят из NuGet.

## Сборка

```
dotnet test tests/VKTools.Core.Tests                                              # автотесты ядра
dotnet build src/VKTools.Revit/VKTools.Revit.csproj -c Release -p:RevitVersion=2022   # надстройка под год
dotnet build installer/VKTools.Installer.wixproj -c Release                       # MSI (Windows; после сборок 2021, 2022, 2024)
```

Выход надстройки: `src/VKTools.Revit/bin/Release/<год>/` — `VKTools.dll`, `VKTools.Core.dll`, `config/`, `VKTools.addin`.
MSI: `installer/bin/Release/VKTools-<версия>.msi`.

С `-p:CI=true` предупреждения компилятора и анализаторов считаются ошибками — так собирает CI.

### Ручная установка для отладки

```
%AppData%\Autodesk\Revit\Addins\2022\VKTools.addin
%AppData%\Autodesk\Revit\Addins\2022\VKTools\   ← содержимое bin/Release/2022 без VKTools.addin
```

## Структура

```
config/                     встроенные правила CSV (едут в установку)
src/VKTools.Core/           логика без Revit: Text/, Config/, Checks/, Logging/, Settings/
src/VKTools.Revit/
    App.cs                  вкладка и кнопки
    Commands/               IExternalCommand, доступность кнопок
    Checks/                 проверки модели (IModelCheck) и их запуск
    Compat/                 различия API между версиями Revit
    Infrastructure/         пути, лог, настройки, чтение параметров
    UI/                     окна WPF, ExternalEvent для выделения
installer/                  WiX: Package.wxs, RevitYear.wxi
tests/VKTools.Core.Tests/   xUnit
docs/                       документация; docs/discovery — итоги Discovery
```

## Правила кода

- Параметры — только по GUID (`KnownParameters`, CSV-правила). Имя — для текста отчёта.
- ElementId — через `RevitCompat` (`ToLong()`, `ToElementId()`), никогда `IntegerValue` / `Value` напрямую.
- Новая константа версии или различие API — только в `Compat/`.
- Команда, которая не пишет в модель, — `[Transaction(TransactionMode.ReadOnly)]`.
- Нет данных — не придумываем: значение не меняется, строка в отчёте.
- Логику, которую можно проверить без Revit, кладём в `VKTools.Core` и покрываем тестом.
- Строки для пользователя — по-русски, с действием: что сделать и где в Revit.
- Комментарии — зачем, а не что.

## Как добавить проверку модели

1. Класс в `src/VKTools.Revit/Checks/`, реализует `IModelCheck` (`Name`, `Run(CheckContext)`).
2. Строки — `CheckFinding(Name, Severity, сообщение)` с `Current`, `Expected`, `Action`; элемент — `.Describe(element)`.
3. Много строк — через `FindingLimiter`.
4. Если нужны новые правила — `RuleFileSpec` в `RuleTables`, разбор, CSV в `config/` с `#schema=1`, файл в `installer/RevitYear.wxi`.
5. Зарегистрировать в `ModelCheckRunner`.
6. Тест на логику в Core, случай в чек-листе [TESTING.md](TESTING.md).

## Ветки и PR

- `main` — то, что можно ставить. Работа — в ветках, слияние через PR с зелёным CI.
- Версия — `Version` в `Directory.Build.props`; при выпуске — запись в `CHANGELOG.md`.
