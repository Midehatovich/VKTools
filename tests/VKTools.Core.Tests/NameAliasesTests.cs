using System.Collections.Generic;
using VKTools.Core.Spec;
using Xunit;

namespace VKTools.Core.Tests
{
    /// <summary>«Одно наименование» (0.17.4): правила «вариант → основное» в модели.</summary>
    public class NameAliasesTests
    {
        [Fact]
        public void Правило_применяется_и_переживает_запись_в_текст()
        {
            var aliases = new Dictionary<string, string>();
            NameAliases.Add(aliases, new[] { "Труба ø219x6", "Труба неоцинкованная ø219x6" }, "Труба неоцинкованная ø219x6");
            Dictionary<string, string> back = NameAliases.Parse(NameAliases.Format(aliases));
            Assert.Equal("Труба неоцинкованная ø219x6", NameAliases.Map(back, "Труба ø219x6"));
            Assert.Equal("Труба неоцинкованная ø219x6", NameAliases.Map(back, "Труба неоцинкованная ø219x6"));
            Assert.Equal("Другое; с точкой с запятой", NameAliases.Map(back, "Другое; с точкой с запятой"));
            Assert.Single(back);
        }

        [Fact]
        public void Перевернутый_выбор_не_дает_цепочек_и_петель()
        {
            var aliases = new Dictionary<string, string>();
            NameAliases.Add(aliases, new[] { "A", "B" }, "B");      // A → B
            NameAliases.Add(aliases, new[] { "B", "C" }, "C");      // B → C, и A → C
            Assert.Equal("C", NameAliases.Map(aliases, "A"));
            Assert.Equal("C", NameAliases.Map(aliases, "B"));
            NameAliases.Add(aliases, new[] { "C", "A" }, "A");      // передумали: всё → A
            Assert.Equal("A", NameAliases.Map(aliases, "C"));
            Assert.Equal("A", NameAliases.Map(aliases, "B"));
            Assert.Equal("A", NameAliases.Map(aliases, "A"));
            Assert.False(aliases.ContainsKey("A"));
        }

        [Fact]
        public void Пустой_и_битый_текст_без_правил()
        {
            Assert.Empty(NameAliases.Parse(null));
            Assert.Empty(NameAliases.Parse("без табуляции\n\tпусто слева\nпусто справа\t\nX\tX\n"));
        }
    }
}
