using System;

namespace VKTools.Core.Config
{
    /// <summary>
    /// Параметры, на которых завязана логика плагина. GUID — из ФОП отдела LSR_ФОП_02 (проверено 2026-10-07).
    /// Имена — только для сообщений: ищем всегда по GUID (решение D-003).
    /// </summary>
    public static class KnownParameters
    {
        public static readonly ParameterRef AdskName =
            new ParameterRef("ADSK_Наименование", new Guid("e6e0f5cd-3e26-485b-9342-23882b20eb43"));

        public static readonly ParameterRef AdskLevel =
            new ParameterRef("ADSK_Этаж", new Guid("9eabf56c-a6cd-4b5c-a9d0-e9223e19ea3f"));

        public static readonly ParameterRef AdskGrouping =
            new ParameterRef("ADSK_Группирование", new Guid("3de5f1a4-d560-4fa8-a74f-25d250fb3401"));

        /// <summary>Тип данных «autodesk.spec.aec:number» (не SpecTypeId.Number), GUID проверен в КрМа3 2026-10-20.</summary>
        public static readonly ParameterRef AdskQuantity =
            new ParameterRef("ADSK_Количество", new Guid("8d057bb3-6ccd-4655-9165-55526691fe3a"));

        public static readonly ParameterRef LsrSystemName =
            new ParameterRef("LSR_Имя системы", new Guid("0cc98b64-809e-45f3-9ea7-553b2d6f21ca"));
    }
}
