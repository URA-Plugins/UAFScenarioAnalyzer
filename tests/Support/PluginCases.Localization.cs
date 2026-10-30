using System.Globalization;
using System.Reflection;
using Gallop;
using UmamusumeResponseAnalyzer;
using UmamusumeResponseAnalyzer.Entities;
using UmamusumeResponseAnalyzer.TerminalGui;
using HostProgram = UmamusumeResponseAnalyzer.UmamusumeResponseAnalyzer;

static partial class PluginCases
{
    public static void VerifyLocalizedTraining(PluginCase pluginCase, WorkspaceSmokeSession ui)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var (culture, speed, wit, friend) in new[]
            {
                ("zh-CN", "速", "智", "友"),
                ("en-US", "Spd", "Wit", "Pal"),
                ("ja-JP", "速", "賢", "友")
            })
            {
                HostProgram.ApplyCultureInfo(CultureInfo.GetCultureInfo(culture));
                var localized = pluginCase with
                {
                    Panel = pluginCase.Panel! with
                    {
                        CreateResponse = () => CreateLocalizedTrainingResponse(pluginCase.Panel!.CreateResponse),
                        CreateUpdatedResponse = () => CreateLocalizedTrainingResponse(pluginCase.Panel!.CreateUpdatedResponse!),
                        VisibleText = $"[{friend}]Friend",
                        History = null,
                        VerifyFramebuffer = frame => VerifyPartnerFramebuffer(frame, pluginCase.Id, speed, wit, friend)
                    }
                };
                localized.Run(ui);
                Console.WriteLine($"PASS {pluginCase.Id} localized training {culture}");
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
            HostProgram.ApplyResourceCulture(previousUiCulture);
        }
    }

    static object CreateLocalizedTrainingResponse(Func<object> createResponse)
    {
        InitializePartnerDatabase();
        var response = createResponse();
        var data = response.GetType().GetField("data")!.GetValue(response)!;
        AddPartners(
            (SingleModeChara)data.GetType().GetField("chara_info")!.GetValue(data)!,
            (SingleModeHomeInfo)data.GetType().GetField("home_info")!.GetValue(data)!);
        return response;
    }

    static void InitializePartnerDatabase() => InitializeDatabase(
        "localized partners", [],
        [
            new BaseName(1001, "Mismatch", "Mismatch"),
            new BaseName(1002, "Ready", "Ready"),
            new BaseName(1003, "Friend", "Friend"),
            new BaseName(1004, "Low", "Low"),
            new BaseName(1005, "Group", "Group"),
            new SupportCardName(30001, "Mismatch", "Mismatch", 106, 1001),
            new SupportCardName(30002, "Ready", "Ready", 101, 1002),
            new SupportCardName(30003, "Friend", "Friend", 0, 1003),
            new SupportCardName(30004, "Low", "Low", 101, 1004),
            new SupportCardName(30241, "Group", "Group", 0, 1005)
        ], new Dictionary<int, string>(), []);

    static void AddPartners(SingleModeChara chara, SingleModeHomeInfo home)
    {
        chara.support_card_array =
        [
            new() { position = 1, support_card_id = 30001 },
            new() { position = 2, support_card_id = 30002 },
            new() { position = 3, support_card_id = 30003 },
            new() { position = 4, support_card_id = 30004 },
            new() { position = 5, support_card_id = 30241 }
        ];
        chara.evaluation_info_array =
        [
            .. Enumerable.Range(1, 5).Select(position => new EvaluationInfo
            {
                target_id = position,
                evaluation = position == 4 ? 60 : 90
            })
        ];
        home.command_info_array[0].training_partner_array = [1, 2, 4, 3, 5];
    }

    static void VerifyPartnerFramebuffer(string screen, string pluginId, string speed, string wit, string friend)
    {
        var previous = -1;
        foreach (var token in new[] { $"[{friend}]Friend", $"[{friend}]Group", $"[{speed}]Ready", $"[{speed}]Low", $"[{wit}]Mismatch" })
        {
            var index = screen.IndexOf(token, StringComparison.Ordinal);
            if (index <= previous)
                throw new InvalidOperationException($"{pluginId}: localized partner '{token}' is missing or has the wrong priority.");
            previous = index;
        }
        if (pluginId == "OldScenarioAnalyzer")
            return; // OldScenarioAnalyzer exposes friendship priority through row order.
        foreach (var (name, shining) in new[] { ("Ready", true), ("Low", false), ("Mismatch", false), ("Friend", false), ("Group", false) })
        {
            var line = screen.Split('\n').Distinct().Single(row => row.Contains("]" + name, StringComparison.Ordinal));
            if (line.Contains('★') != shining)
                throw new InvalidOperationException($"{pluginId}: {name} friendship training changed with the UI language; 30241 must keep this scenario's existing rule.");
        }
    }

}
