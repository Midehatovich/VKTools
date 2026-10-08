# Технологии

| Слой | Выбор | Версия | Почему |
|---|---|---|---|
| Язык | C# | 10 (`LangVersion` зафиксирован) | одна версия языка для net48 и net8, без функций, которых нет в net48 |
| Платформа | .NET Framework | 4.8 | Revit 2021–2024 |
| | .NET | 8 | Revit 2025–2026 |
| Revit API | Nice3point.Revit.Api.RevitAPI / RevitAPIUI | по году, см. [DEPENDENCIES](DEPENDENCIES.md) | ссылочные сборки Autodesk в NuGet: сборка без установленного Revit |
| UI | WPF (в коде, без XAML) | из платформы | штатный UI Revit-надстроек, без сторонних библиотек |
| Правила | CSV (UTF-8, «;», `#schema=N`) | схема 1 | отдел правит в Excel, как resources.csv из Dynamo-скриптов |
| Настройки | XML (System.Xml.Linq) | из платформы | без сторонних сериализаторов |
| Тесты | xUnit | 2.9.3 | стандарт для .NET, Apache-2.0 |
| Анализаторы | .NET analyzers (`AnalysisLevel=latest-recommended`) + `.editorconfig` | из SDK | в CI предупреждения = ошибки |
| Установщик | WiX Toolset (MSI) | 5.0.2 | MS-RL; MSI в профиль пользователя без прав администратора |
| SDK | .NET SDK | 8.0.424 в CI, `global.json` ≥ 8.0.100 | одна SDK для net48 и net8 |
| CI | GitHub Actions | ubuntu-24.04, windows-2025 | тесты и сборки на Linux, MSI на Windows |

Сторонних библиотек в поставке нет: в установке только `VKTools.dll`, `VKTools.Core.dll`, CSV и манифест.
В одном процессе Revit живут BIM2B, ModPlus и другие надстройки; любая общая DLL разных версий —
частая причина «у коллеги не запускается» (D-004).
