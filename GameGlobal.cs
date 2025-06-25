using System.Collections.Frozen;
using static UAFScenarioAnalyzer.i18n.Game;

namespace UAFScenarioAnalyzer
{
    public class GGlobal
    {
        public static readonly FrozenDictionary<int, string> TrainNames = new Dictionary<int, string>
        {
            { 101, I18N_Speed },
            { 105, I18N_Stamina },
            { 102, I18N_Power },
            { 103, I18N_Nuts },
            { 106, I18N_Wiz }
        }.ToFrozenDictionary();
    }
}
