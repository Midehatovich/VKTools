using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using VKTools.Core.Config;
using VKTools.Core.Spec;
using VKTools.Revit.Compat;
using VKTools.Revit.Infrastructure;

namespace VKTools.Revit.Spec
{
    /// <summary>
    /// Настройки проекта в модели (REQ-CFG-005, D-033): Extensible Storage на элементе DataStorage, одно поле —
    /// словарь «ключ → текст». Схема не меняется при новых настройках: новые ключи в том же словаре.
    /// В совместной модели элемент один на всех; сохранение его занимает, чтение — нет.
    /// </summary>
    internal static class SpecSettingsStore
    {
        public const string TransactionName = "VK Tools: настройки проекта";

        private static readonly Guid SchemaGuid = new Guid("6b0f3c52-8e1d-4f7a-9c35-2d4e8a61b7f0");
        private const string SchemaName = "VKToolsProjectSettings";
        private const string FieldValues = "Values";

        /// <summary>Настройки из модели; нет хранилища — пустые (всё по умолчанию).</summary>
        public static SpecProjectSettings Read(Document doc, out int storages)
        {
            List<DataStorage> found = Storages(doc, out Schema? schema);
            storages = found.Count;
            if (schema == null || found.Count == 0) return new SpecProjectSettings();
            Entity entity = found[0].GetEntity(schema);
            IDictionary<string, string> values = entity.Get<IDictionary<string, string>>(schema.GetField(FieldValues));
            return new SpecProjectSettings(values);
        }

        /// <summary>
        /// Записать (внутри открытой транзакции). null — записано; иначе причина: хранилище занято коллегой
        /// или изменено в центральной модели.
        /// </summary>
        public static string? Write(Document doc, SpecProjectSettings settings)
        {
            Schema schema = Schema.Lookup(SchemaGuid) ?? Build();
            List<DataStorage> found = Storages(doc, out _);
            DataStorage? storage = found.FirstOrDefault();
            if (storage != null && doc.IsWorkshared)
            {
                try
                {
                    if (WorksharingUtils.GetCheckoutStatus(doc, storage.Id, out string owner) == CheckoutStatus.OwnedByOtherUser)
                    {
                        return $"настройки проекта сейчас занял «{owner}»: сохранить можно после его синхронизации с освобождением элементов";
                    }
                    ModelUpdatesStatus status = WorksharingUtils.GetModelUpdatesStatus(doc, storage.Id);
                    if (status == ModelUpdatesStatus.UpdatedInCentral || status == ModelUpdatesStatus.DeletedInCentral)
                    {
                        return "настройки проекта изменены в хранилище: сначала «Обновить до последней версии»";
                    }
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // Статус не прочитали — Revit сам откажет при фиксации, если элемент чужой
                }
            }
            storage ??= DataStorage.Create(doc);
            // Набор параметров закрепляется при первом сохранении: иначе модель «Р_» с сохранёнными настройками читалась бы как ADSK
            if (settings.Get(ParamProfile.SettingsKey) == null) settings.Set(ParamProfile.SettingsKey, ParamProfile.Store(ParamProfile.Active));
            var entity = new Entity(schema);
            entity.Set<IDictionary<string, string>>(schema.GetField(FieldValues), new Dictionary<string, string>(settings.Values));
            storage.SetEntity(entity);
            return null;
        }

        /// <summary>Сохранить настройку модуля отдельной операцией (кнопка «Сохранить»). null — сохранено.</summary>
        public static string? Save(Document doc, SpecOptions options, SpecDefaults defaults)
        {
            using (var transaction = new Transaction(doc, TransactionName))
            {
                transaction.Start();
                string? problem = Store(doc, options, defaults);
                if (problem != null)
                {
                    transaction.RollBack();
                    return problem;
                }
                TransactionStatus status = transaction.Commit();
                return status == TransactionStatus.Committed ? null : $"Revit не зафиксировал настройки ({status})";
            }
        }

        /// <summary>Положить настройку модуля в модель (внутри открытой транзакции).</summary>
        public static string? Store(Document doc, SpecOptions options, SpecDefaults defaults)
        {
            SpecProjectSettings settings = Read(doc, out _);
            options.StoreTo(settings, defaults, Environment.UserName, DateTime.Now);
            return Write(doc, settings);
        }

        /// <summary>Хранилища VK Tools в модели, новое первым. Два — бывает, если двое создали их до синхронизации.</summary>
        private static List<DataStorage> Storages(Document doc, out Schema? schema)
        {
            schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return new List<DataStorage>();
            return new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).WherePasses(new ExtensibleStorageFilter(SchemaGuid))
                .Cast<DataStorage>().OrderByDescending(s => s.Id.ToLong()).ToList();
        }

        private static Schema Build()
        {
            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName(SchemaName);
            builder.SetDocumentation("VK Tools: настройки проекта (перенос во вложенные, таблица групп ADSK_Группирование).");
            builder.SetVendorId("VKTOOLS");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddMapField(FieldValues, typeof(string), typeof(string));
            return builder.Finish();
        }
    }

    /// <summary>Настройки модуля для окна и анализа: по умолчанию (файлы правил) и из модели.</summary>
    internal static class SpecSettingsLoader
    {
        public static (SpecOptions Options, SpecDefaults Defaults) Load(Document doc, SpecModule module)
        {
            ConfigLoader loader = Host.CreateConfigLoader(Host.LoadSettings());
            SpecProjectSettings stored = SpecSettingsStore.Read(doc, out int storages);
            // Набор параметров модели — до умолчаний: от него зависят параметры по умолчанию всех модулей
            ParamProfile.Active = ParamProfile.Resolve(stored);
            SpecDefaults defaults = LoadDefaults(loader);
            var issues = new List<ConfigIssue>();
            SpecOptions options = SpecOptions.Load(module, stored, defaults, issues, out string? blocked);
            defaults.Issues.AddRange(issues);
            defaults.ProjectBlocked = blocked;
            if (storages > 1)
            {
                Host.Log.Warn($"В модели «{doc.Title}» хранилищ настроек VK Tools: {storages}; читается последнее созданное.");
            }
            return (options, defaults);
        }

        private static SpecDefaults LoadDefaults(ConfigLoader loader)
        {
            var defaults = new SpecDefaults();
            LoadedTable nested = loader.Load(SpecRuleTables.NestedParameters);
            defaults.NestedSource = Source(nested);
            defaults.NestedBlocked = nested.WriteBlocked ? $"{nested.Spec.FileName}: {nested.WriteBlockedReason}" : null;
            defaults.Issues.AddRange(nested.Issues);
            if (nested.Table != null)
            {
                ParsedRules<ParameterRef> parsed = SpecRuleTables.ParseNestedParameters(nested.Table);
                defaults.Issues.AddRange(parsed.Issues);
                // Свой набор «Р_»: ADSK_Этаж → Р_Этаж, VK_Сокращение системы → Р_Сокращение системы, LSR_* — не переносятся
                defaults.NestedParams.AddRange(parsed.Items.Select(ParamProfile.MapToActive).Where(p => p != null).Select(p => ParamKey.Shared(p!.Name, p.Guid)).Distinct());
            }

            LoadedTable grouping = loader.Load(SpecRuleTables.Grouping);
            defaults.GroupingSource = Source(grouping);
            defaults.GroupingBlocked = grouping.WriteBlocked ? $"{grouping.Spec.FileName}: {grouping.WriteBlockedReason}" : null;
            defaults.Issues.AddRange(grouping.Issues);
            if (grouping.Table != null)
            {
                GroupingTable table = SpecRuleTables.ParseGrouping(grouping.Table, defaults.Issues);
                defaults.GroupRules.AddRange(table.Rules);
            }
            return defaults;
        }

        public static string Source(LoadedTable loaded)
        {
            string source = loaded.Source == ConfigSourceKind.Department ? "отдел" : "встроенные";
            return loaded.Table != null
                ? $"{loaded.Spec.FileName}: {source}, {loaded.SourcePath} ({loaded.EncodingName})"
                : $"{loaded.Spec.FileName}: НЕ ЗАГРУЖЕН";
        }
    }
}
