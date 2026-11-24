using System;
using System.Linq;
using VKTools.Core.Config;
using VKTools.Core.Spec;
using Xunit;

namespace VKTools.Core.Tests
{
    /// <summary>0.15.0: «Р_» нередактируемые вручную — новые GUID, старые переводятся (решение пользователя 2026-11-23).</summary>
    public class OwnParamsLockTests
    {
        [Fact]
        public void В_ФОП_все_Р_нередактируемые_VK_Сокращение_редактируемое()
        {
            string[] lines = ParamProfile.SharedParameterFile().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.StartsWith("PARAM\t", StringComparison.Ordinal)).ToArray();
            foreach (OwnParamDef d in ParamProfile.OwnDefinitions)
            {
                string[] cols = lines.Single(l => l.Contains(d.Guid.ToString("D"))).Split('\t');
                Assert.Equal("0", cols[8]);
            }
            Assert.Equal("1", lines.Single(l => l.Contains(KnownParameters.VkSystem.Guid.ToString("D"))).Split('\t')[8]);
        }

        [Fact]
        public void Старые_GUID_ведут_на_ту_же_роль_и_новый_GUID()
        {
            Assert.Equal(12, ParamProfile.LegacyOwnGuids.Count);
            foreach (var pair in ParamProfile.LegacyOwnGuids)
            {
                Assert.Equal(pair.Value, ParamProfile.RoleOf(pair.Key));
                Assert.Equal(ParamProfile.Own(pair.Value).Guid, ParamProfile.Current(pair.Key));
                Assert.NotEqual(pair.Key, ParamProfile.Own(pair.Value).Guid);
            }
            Guid adsk = ParamProfile.Adsk(ParamRole.Mark).Guid;
            Assert.Equal(adsk, ParamProfile.Current(adsk));
        }

        [Fact]
        public void Сохранённый_ключ_со_старым_GUID_читается_как_новый()
        {
            ParamKey? key = ParamKey.Parse("bbe8adf8-7aa7-4b69-aa04-82a50f30f4df", "Р_Наименование");
            Assert.NotNull(key);
            Assert.Equal(ParamProfile.Own(ParamRole.Name).Guid, key!.Guid);
        }
    }
}
