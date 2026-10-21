using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using VKTools.Core.Calc;
using VKTools.Core.Checks;
using VKTools.Core.Spec;

namespace VKTools.Revit.Calc
{
    internal enum CalcOpKind
    {
        Create,
        Update,
        DeleteOrphan,
        DeleteDuplicate,
    }

    /// <summary>Значение, которое должна получить позиция: имя параметра, что записать, обязателен ли.</summary>
    internal sealed class CalcItemValue
    {
        public CalcItemValue(string parameter, object value, bool required)
        {
            Parameter = parameter;
            Value = value;
            Required = required;
        }

        public string Parameter { get; }

        /// <summary>string, double или <see cref="ParamValue"/> (копия значения с трубы — тип хранения как у трубы).</summary>
        public object Value { get; }

        public bool Required { get; }
    }

    /// <summary>Одно изменение параметра: для «было → станет» и для записи.</summary>
    internal sealed class CalcWrite
    {
        public CalcWrite(Parameter? parameter, string name, string old, ParamValue value)
        {
            Parameter = parameter;
            Name = name;
            Old = old;
            Value = value;
        }

        /// <summary>null — смена типоразмера на <see cref="IdValue"/>.</summary>
        public Parameter? Parameter { get; }

        public string Name { get; }

        public string Old { get; }

        public ParamValue Value { get; }

        public string New => CalcParams.Display(Value);

        /// <summary>Значение-элемент: типоразмер (без параметра) или стадия возведения; <see cref="Value"/> — его имя для отчёта.</summary>
        public ElementId? IdValue { get; set; }
    }

    /// <summary>Служебная позиция: создать, обновить или удалить.</summary>
    internal sealed class CalcOp
    {
        public CalcOp(CalcOpKind kind, string key)
        {
            Kind = kind;
            Key = key;
        }

        public CalcOpKind Kind { get; }

        public string Key { get; }

        public CalcTarget? Target { get; set; }

        public long? ItemId { get; set; }

        /// <summary>SYS_Кол-во вручную у существующей позиции.</summary>
        public string ManualText { get; set; } = string.Empty;

        /// <summary>ADSK_Количество сейчас (для «было»).</summary>
        public string OldQuantity { get; set; } = string.Empty;

        public double Final { get; set; }

        public CalcMode Mode { get; set; }

        public string Note { get; set; } = string.Empty;

        /// <summary>Значения экземпляра (параметры типа пишет <see cref="CalcTypeOp"/>).</summary>
        public List<CalcItemValue> Values { get; } = new List<CalcItemValue>();

        /// <summary>Что изменится у существующей позиции (по анализу).</summary>
        public List<CalcWrite> Changes { get; } = new List<CalcWrite>();

        /// <summary>Позицию нельзя тронуть: занята, в группе, нет обязательного параметра.</summary>
        public string? Error { get; set; }

        /// <summary>Есть что делать: создать, удалить или изменить.</summary>
        public bool HasWork => Kind != CalcOpKind.Update || Changes.Count > 0;
    }

    /// <summary>Типоразмер ресурса (тип = ресурс): создать копией базового или обновить значения параметров типа.</summary>
    internal sealed class CalcTypeOp
    {
        public CalcTypeOp(CalcResource resource)
        {
            Resource = resource;
        }

        public CalcResource Resource { get; }

        public string Code => Resource.Code;

        public long? SymbolId { get; set; }

        public List<CalcItemValue> Values { get; } = new List<CalcItemValue>();

        public List<CalcWrite> Changes { get; } = new List<CalcWrite>();

        public string? Error { get; set; }

        public bool HasWork => !SymbolId.HasValue || Changes.Count > 0;
    }

    /// <summary>Параметр проекта, которому не хватает категории «Обобщённые модели»: «Применить» её добавит.</summary>
    internal sealed class CalcBindingFix
    {
        public CalcBindingFix(string parameter, Definition definition, bool instance, int categories)
        {
            Parameter = parameter;
            Definition = definition;
            Instance = instance;
            Categories = categories;
        }

        public string Parameter { get; }

        public Definition Definition { get; }

        public bool Instance { get; }

        public int Categories { get; }
    }

    /// <summary>Спецификация в модели — для выбора в окне.</summary>
    internal sealed class CalcScheduleInfo
    {
        public CalcScheduleInfo(string name, long id, int pipes, string category)
        {
            Name = name;
            Id = id;
            Pipes = pipes;
            Category = category;
        }

        public string Name { get; }

        public long Id { get; }

        public int Pipes { get; }

        /// <summary>«несколько категорий» или имя категории спецификации.</summary>
        public string Category { get; }
    }

    /// <summary>Область расчёта: спецификация (её трубы и её фильтры) или вся модель.</summary>
    internal sealed class CalcScope
    {
        public CalcScope(string name, ViewSchedule? schedule)
        {
            Name = name;
            Schedule = schedule;
        }

        public string Name { get; }

        public ViewSchedule? Schedule { get; }

        /// <summary>Поля фильтров спецификации: их значения копируются с труб в позиции.</summary>
        public List<string> CopyNames { get; } = new List<string>();

        public List<PipeSample> Pipes { get; } = new List<PipeSample>();
    }

    /// <summary>План «Расчётных позиций»: что посчитано и что будет записано. Строится заново перед каждым действием.</summary>
    internal sealed class CalcPlan
    {
        public CalcPlan(CalcOptions options, CalcRuleSet rules)
        {
            Options = options;
            Rules = rules;
        }

        public CalcOptions Options { get; }

        public CalcRuleSet Rules { get; }

        public CalcResult Result { get; set; } = new CalcResult();

        public List<CalcScope> Scopes { get; } = new List<CalcScope>();

        public List<CalcScheduleInfo> Schedules { get; } = new List<CalcScheduleInfo>();

        /// <summary>Текстовые параметры труб — подсказки для «Параметр системы».</summary>
        public SortedSet<string> SystemCandidates { get; } = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);

        public string FamilyName { get; set; } = CalcFamilyBuilder.DefaultFamily;

        public long? FamilyId { get; set; }

        /// <summary>Семейства нет: «Применить» создаст и загрузит его (REQ-CALC-006).</summary>
        public bool CreateFamily { get; set; }

        public long? BaseSymbolId { get; set; }

        /// <summary>Параметры, которые пишутся в тип (тип = ресурс).</summary>
        public HashSet<string> TypeLevel { get; } = new HashSet<string>(StringComparer.Ordinal);

        public List<CalcTypeOp> TypeOps { get; } = new List<CalcTypeOp>();

        public List<CalcOp> Ops { get; } = new List<CalcOp>();

        public List<CalcBindingFix> Bindings { get; } = new List<CalcBindingFix>();

        /// <summary>Значения полей фильтров по ключу (ключ — в <see cref="CalcTarget.Copy"/>).</summary>
        public Dictionary<string, ParamValue> CopyValues { get; } = new Dictionary<string, ParamValue>(StringComparer.Ordinal);

        public CalcIssueList Issues { get; } = new CalcIssueList();

        public List<string> Notes { get; } = new List<string>();

        public List<string> RuleSources { get; } = new List<string>();

        public string? WriteBlockedReason { get; set; }

        /// <summary>Сколько всего действий: семейство, привязки, типоразмеры, позиции.</summary>
        public int ChangeCount =>
            (CreateFamily ? 1 : 0) + Bindings.Count + TypeOps.Count(t => t.Error == null && t.HasWork) + Ops.Count(o => o.Error == null && o.HasWork);

        public int Unchanged => Ops.Count(o => o.Kind == CalcOpKind.Update && o.Error == null && o.Changes.Count == 0);

        public int ErrorCount => Issues.Items.Where(i => i.Severity == Severity.Error).Sum(i => i.Count);

        public int WarningCount => Issues.Items.Where(i => i.Severity == Severity.Warning).Sum(i => i.Count);
    }
}
