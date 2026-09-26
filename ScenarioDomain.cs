using System.Collections.Frozen;
using Gallop;
using UmamusumeResponseAnalyzer;

namespace UAFScenarioAnalyzer;

internal sealed class SingleModeTurnData
{
    public SingleModeChara chara_info = null!;
    public SingleModeHomeInfo home_info = null!;
    public SingleModeSportDataSet sport_data_set = null!;

    public static SingleModeTurnData From(SingleModeSportCheckEventResponse.CommonResponse commonResponse) => new()
    {
        chara_info = commonResponse.chara_info,
        home_info = commonResponse.home_info,
        sport_data_set = commonResponse.sport_data_set
    };
}

internal class TurnInfo(SingleModeTurnData resp)
{
    SingleModeChara CharaInfo => resp.chara_info;

    public int SpeedRevised => ReviseOver1200(CharaInfo.speed);
    public int StaminaRevised => ReviseOver1200(CharaInfo.stamina);
    public int PowerRevised => ReviseOver1200(CharaInfo.power);
    public int GutsRevised => ReviseOver1200(CharaInfo.guts);
    public int WizRevised => ReviseOver1200(CharaInfo.wiz);
    public int[] Stats => [CharaInfo.speed, CharaInfo.stamina, CharaInfo.power, CharaInfo.guts, CharaInfo.wiz];
    public int[] StatsRevised => [SpeedRevised, StaminaRevised, PowerRevised, GutsRevised, WizRevised];
    public int[] MaxStatsRevised =>
    [
        ReviseOver1200(CharaInfo.max_speed),
        ReviseOver1200(CharaInfo.max_stamina),
        ReviseOver1200(CharaInfo.max_power),
        ReviseOver1200(CharaInfo.max_guts),
        ReviseOver1200(CharaInfo.max_wiz)
    ];
    public int Turn => CharaInfo.turn;
    public int Year => (Turn - 1) / 24 + 1;
    public int Vital => CharaInfo.vital;
    public int MaxVital => CharaInfo.max_vital;
    public FrozenDictionary<int, int> SupportCards => CharaInfo.support_card_array.ToDictionary(x => x.position, x => x.support_card_id).ToFrozenDictionary();
    public FrozenDictionary<int, EvaluationInfo> Evaluations => CharaInfo.evaluation_info_array.ToDictionary(x => x.target_id, x => x).ToFrozenDictionary();
    public int Month => ((Turn - 1) % 24) / 2 + 1;
    public string HalfMonth => Turn % 2 == 0 ? "后半" : "前半";
    public SingleModeTurnData GetCommonResponse() => resp;

    static int ReviseOver1200(int value) => value > 1200 ? value * 2 - 1200 : value;
}

internal sealed class TurnInfoUAF(SingleModeTurnData resp) : TurnInfo(resp)
{
    public static readonly IReadOnlyList<int> LinkCharacterIds = Array.AsReadOnly([1035, 1027, 1048, 1072, 1077, 9044]);

    public IEnumerable<TrainingSport> TrainingArray { get; } = resp.sport_data_set.training_array.Select(x => new TrainingSport(x)).ToArray();
    public IReadOnlyList<ParsedSingleModeSportCommandInfo> CommandInfoArray { get; } = CreateCommandInfoArray(resp);
    public int AvailableTalkCount { get; } = resp.sport_data_set.item_id_array.Count(x => x == 6);

    public Dictionary<SportColor, List<TrainingSport>> SportsByColor { get; } =
        resp.sport_data_set.training_array.Select(x => new TrainingSport(x)).GroupBy(x => x.Color).ToDictionary(x => x.Key, x => x.ToList());
    public int BlueLevel => SportsByColor[SportColor.Blue].Sum(x => x.SportRank);
    public int RedLevel => SportsByColor[SportColor.Red].Sum(x => x.SportRank);
    public int YellowLevel => SportsByColor[SportColor.Yellow].Sum(x => x.SportRank);
    public bool IsRankGainIncreased => CommandInfoArray.Any(x => x.IsCurrentRankGainIncreased);

    static ParsedSingleModeSportCommandInfo[] CreateCommandInfoArray(SingleModeTurnData resp)
    {
        var commands = resp.sport_data_set.command_info_array
            .Where(x => x.command_id > 1000)
            .Select(x => new ParsedSingleModeSportCommandInfo(resp, x.command_id))
            .ToArray();
        var isRankGainIncreased = commands.Any(x => x.IsCurrentRankGainIncreased);
        foreach (var command in commands)
            command.IsRankGainIncreased = isRankGainIncreased;
        return commands;
    }

    public sealed class ParsedSingleModeSportCommandInfo : CommandInfo
    {
        int ActualGainRankWithoutBuff { get; }
        public SportColor Color { get; }
        public int SportRank { get; }
        public int GainRank { get; }
        public int ActualGainRank => IsRankGainIncreased ? ActualGainRankWithoutBuff + 3 : ActualGainRankWithoutBuff;
        public int TotalGainRank { get; }
        public bool IsCurrentRankGainIncreased { get; }
        public bool IsRankGainIncreased { get; set; }

        public ParsedSingleModeSportCommandInfo(SingleModeTurnData resp, int commandId) : base(resp, new TurnInfo(resp), commandId)
        {
            TrainIndex = int.Parse(CommandId.ToString()[3].ToString());
            Color = (SportColor)int.Parse(CommandId.ToString()[1].ToString());
            SportRank = resp.sport_data_set.training_array.First(x => x.command_id == CommandId).sport_rank;
            var gainRanks = resp.sport_data_set.command_info_array[TrainIndex - 1].gain_sport_rank_array;
            GainRank = gainRanks.First(x => x.command_id == CommandId).gain_rank;
            TotalGainRank = resp.sport_data_set.command_info_array[TrainIndex - 1].gain_sport_rank_array.Sum(x => x.gain_rank);
            var supports = TrainingPartners.Where(x => !x.IsNpc).ToArray();
            var shining = TrainingPartners.Any(x => x.Shining) ? 2 : 1;
            var partnerAdd = supports.Length switch
            {
                0 => 0,
                1 => 1,
                2 => 2,
                3 => 2,
                4 => 3,
                5 => 3,
                _ => 3
            };
            var links = TrainingPartners
                .Select(x => Database.Names.GetRequiredSupportCard(x.CardId).CharaId)
                .Intersect(LinkCharacterIds);
            ActualGainRankWithoutBuff = shining * (3 + partnerAdd) + links.Count();
            IsCurrentRankGainIncreased = GainRank - ActualGainRankWithoutBuff == 3;
        }
    }

    public sealed class TrainingSport
    {
        public int CommandType { get; }
        public int CommandId { get; }
        public int SportRank { get; }
        public SportColor Color { get; }
        public int TrainIndex { get; }

        public TrainingSport(SingleModeSportTraining train)
        {
            CommandType = train.command_type;
            CommandId = train.command_id;
            SportRank = train.sport_rank;
            Color = (SportColor)int.Parse(CommandId.ToString()[1].ToString());
            TrainIndex = int.Parse(CommandId.ToString()[3].ToString());
        }
    }

    public enum SportColor
    {
        Blue = 1,
        Red = 2,
        Yellow = 3
    }
}

internal class CommandInfo
{
    static readonly FrozenDictionary<int, int> ToTrainIndexDefault = new Dictionary<int, int>
    {
        [101] = 0, [105] = 1, [102] = 2, [103] = 3, [106] = 4,
        [2101] = 0, [2201] = 0, [2301] = 0,
        [2102] = 1, [2202] = 1, [2302] = 1,
        [2103] = 2, [2203] = 2, [2303] = 2,
        [2104] = 3, [2204] = 3, [2304] = 3,
        [2105] = 4, [2205] = 4, [2305] = 4
    }.ToFrozenDictionary();
    static readonly FrozenDictionary<int, int> ToTrainIdDefault = new Dictionary<int, int>
    {
        [101] = 101, [105] = 105, [102] = 102, [103] = 103, [106] = 106,
        [2101] = 101, [2201] = 101, [2301] = 101,
        [2102] = 105, [2202] = 105, [2302] = 105,
        [2103] = 102, [2203] = 102, [2303] = 102,
        [2104] = 103, [2204] = 103, [2304] = 103,
        [2105] = 106, [2205] = 106, [2305] = 106
    }.ToFrozenDictionary();

    public int CommandId { get; }
    public int TrainIndex { get; set; }
    public int TrainLevel { get; }
    public IEnumerable<TrainingPartner> TrainingPartners { get; }

    public CommandInfo(SingleModeTurnData resp, TurnInfo turn, int commandId)
    {
        CommandId = commandId;
        if (ToTrainIndexDefault.TryGetValue(commandId, out var trainIndex))
            TrainIndex = trainIndex + 1;
        var training = resp.chara_info.training_level_info_array.FirstOrDefault(x => x.command_id == CommandId);
        TrainLevel = training != default ? training.level : 0;
        var normalCommand = resp.home_info.command_info_array.FirstOrDefault(x => x.command_id == CommandId)
            ?? resp.home_info.command_info_array.First(x => ToTrainIdDefault.TryGetValue(CommandId, out var trainId) && x.command_id == trainId);
        TrainingPartners = normalCommand.training_partner_array
            .Select(x => new TrainingPartner(turn, x, normalCommand))
            .OrderBy(x => x.Priority)
            .ToArray();
    }
}

internal enum PartnerPriority
{
    友人 = 0,
    闪 = 1,
    羁绊不足 = 2,
    其他 = 3,
    关键NPC = 5,
    默认 = 7
}

internal sealed class TrainingPartner
{
    public PartnerPriority Priority { get; private set; } = PartnerPriority.默认;
    public int Position { get; }
    public int CardId { get; }
    public string Name { get; }
    public int Friendship { get; }
    public bool IsNpc => Position is not (>= 1 and <= 6);
    public bool Shining { get; private set; }

    public TrainingPartner(TurnInfo turn, int partner, SingleModeCommandInfo command)
    {
        Position = partner;
        Friendship = turn.Evaluations.TryGetValue(Position, out var evaluation) ? evaluation.evaluation : 0;

        if (!IsNpc)
        {
            CardId = turn.SupportCards[Position];
            var supportCard = Database.Names.GetRequiredSupportCard(CardId);
            var name = Database.Names.DisplayNickname(CardId);
            if (supportCard.IsFriendCard)
            {
                Priority = PartnerPriority.友人;
            }
            else if (Friendship < 80)
            {
                Priority = PartnerPriority.羁绊不足;
            }

            Shining = Friendship >= 80 && supportCard.Type == CommandInfoTrainId(command.command_id);

            if (Shining)
            {
                Priority = supportCard.IsFriendCard ? PartnerPriority.友人 : PartnerPriority.闪;
            }

            var append = Friendship < 100 ? $" {Friendship}" : string.Empty;
            Name = $"{(Shining ? "★ " : string.Empty)}{name}{append}";
        }
        else
        {
            Priority = Position is >= 100 and < 1000 ? PartnerPriority.关键NPC : PartnerPriority.默认;
            Name = Database.Names.DisplayNickname(Position);
        }

        var tips = command.tips_event_partner_array.Intersect(command.training_partner_array);
        if (tips.Contains(Position))
            Name = $"! {Name}";
    }

    static int CommandInfoTrainId(int commandId) => commandId switch
    {
        2101 or 2201 or 2301 => 101,
        2102 or 2202 or 2302 => 105,
        2103 or 2203 or 2303 => 102,
        2104 or 2204 or 2304 => 103,
        2105 or 2205 or 2305 => 106,
        _ => commandId
    };
}
