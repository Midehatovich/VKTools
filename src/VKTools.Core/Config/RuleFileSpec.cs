using System;
using System.Collections.Generic;

namespace VKTools.Core.Config
{
    /// <summary>Описание файла правил: имя, версия схемы и обязательные колонки.</summary>
    public sealed class RuleFileSpec
    {
        public RuleFileSpec(string fileName, int schemaVersion, params string[] requiredColumns)
        {
            FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
            SchemaVersion = schemaVersion;
            RequiredColumns = requiredColumns ?? Array.Empty<string>();
        }

        public string FileName { get; }

        public int SchemaVersion { get; }

        public IReadOnlyList<string> RequiredColumns { get; }
    }
}
