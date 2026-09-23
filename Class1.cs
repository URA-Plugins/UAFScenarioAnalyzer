using EventLoggerPlugin;
using Gallop;
using MathNet.Numerics.Distributions;
using Terminal.Gui.App;
using UmamusumeResponseAnalyzer.TerminalGui;
using UmamusumeResponseAnalyzer.Plugin;
using static UAFScenarioAnalyzer.i18n.Game;
using static UAFScenarioAnalyzer.i18n.UAF;
using static UAFScenarioAnalyzer.TurnInfoUAF;

namespace UAFScenarioAnalyzer
{
    public class UAFScenarioAnalyzer : IPlugin
    {
        const string WorkspaceTitle = "UAFScenarioAnalyzer";

        ScenarioHistory? history;

        public void Initialize(IPluginContext context)
        {
            history = new(context.Application, WorkspaceTitle, ScenarioHistorySettings.Load());
            context.Analyzers.Register<SingleModeSportCheckEventResponse>(
                AnalyzerKind.Response,
                [EndpointPattern.Exact("/umamusume/single_mode_sport/check_event")],
                invocation => Analyze(invocation.Payload),
                priority: 1);
        }

        public ValueTask DisposeAsync()
        {
            history?.Dispose();
            history = null;
            return ValueTask.CompletedTask;
        }

        public ValueTask Analyze(SingleModeSportCheckEventResponse @event)
        {
            var data = @event.data;
            if (data.chara_info.scenario_id != 7) return ValueTask.CompletedTask;
            var state = data.chara_info.state;
            if (data.home_info?.command_info_array is not null && !(state is 2 or 3)) //根据文本简单过滤防止重复、异常输出
            {
                if ((@event.data.unchecked_event_array != null && @event.data.unchecked_event_array.Length > 0) || @event.data.race_start_info != null) return ValueTask.CompletedTask;
                var key = new ScenarioHistoryKey(
                    data.chara_info.single_mode_chara_id,
                    data.chara_info.turn);
                history?.Publish(key, ParseSportCommandInfo(@event));
            }
            return ValueTask.CompletedTask;
        }

        public async Task ConfigPromptAsync(
            IApplication application,
            CancellationToken cancellationToken = default)
        {
            var settings = await ScenarioHistorySettings.EditAsync(application, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            settings.Save();
            history?.ApplyLimit(settings.HistoryLimit);
        }

        public static WorkspaceContent ParseSportCommandInfo(SingleModeSportCheckEventResponse @event)
        {
            var extInfos = new List<string>();
            var critInfos = new List<string>();
            var turn = new TurnInfoUAF(SingleModeTurnData.From(@event.data));
            var eventLoggerSnapshot = new EventLoggerSnapshot(
                @event.data.chara_info,
                @event.data.unchecked_event_array,
                @event.data.select_index_info_array);
            var round = EventLogger.Current;
            var outOfSequence = round.CurrentTurn != turn.Turn - 1 &&
                round.CurrentTurn != turn.Turn &&
                turn.Turn != 1;

            if (outOfSequence)
            {
                critInfos.Add(string.Format(I18N_WrongTurnAlert, round.CurrentTurn, turn.Turn));
                EventLogger.ResetSession(eventLoggerSnapshot, isFullGame: false);
                round = EventLogger.Current;
            }
            else if (turn.Turn == 1)
            {
                EventLogger.ResetSession(eventLoggerSnapshot, isFullGame: true);
                round = EventLogger.Current;
            }

            //买技能，大师杯剧本年末比赛，会重复显示
            var isRepeat = @event.data.chara_info.playing_state != 1 ||
                outOfSequence ||
                round.CurrentTurn == turn.Turn;
            if (isRepeat)
            {
                critInfos.Add(I18N_RepeatTurn);
                if (turn.Turn >= round.Turns.Length || round.Turns[turn.Turn] is null)
                {
                    // FIXME: 中途开始回合时可能会到这个分支。问题还需要排查
                    critInfos.Add("中途开始回合");
                }
            }
            else
            {
                EventLogger.BeginScenarioTurn(
                    eventLoggerSnapshot,
                    @event.data.chara_info.scenario_id,
                    turn.Turn);
                round = EventLogger.Current;
            }
            var turnStat = round.NewTurnBuilder(turn.Turn);
            var trainItems = new Dictionary<int, SingleModeCommandInfo>
            {
                { 101, @event.data.home_info.command_info_array[0] },
                { 105, @event.data.home_info.command_info_array[1] },
                { 102, @event.data.home_info.command_info_array[2] },
                { 103, @event.data.home_info.command_info_array[3] },
                { 106, @event.data.home_info.command_info_array[4] }
            };
            var trainStats = new TrainStats[5];
            var failureRate = new Dictionary<int, int>();

            // 总属性计算
            var currentFiveValue = new int[]
            {
                @event.data.chara_info.speed,
                @event.data.chara_info.stamina,
                @event.data.chara_info.power ,
                @event.data.chara_info.guts ,
                @event.data.chara_info.wiz ,
            };
            var currentFiveValueRevised = currentFiveValue.Select(x => ScoreUtils.ReviseOver1200(x)).ToArray();
            var totalValue = currentFiveValueRevised.Sum();

            for (var i = 0; i < 5; i++)
            {
                var trainId = EventLoggerPlugin.GameGlobal.TrainIds[i];
                failureRate[trainId] = trainItems[trainId].failure_rate;
                var trainParams = new Dictionary<int, int>()
                {
                    {1,0},
                    {2,0},
                    {3,0},
                    {4,0},
                    {5,0},
                    {30,0},
                    {10,0},
                };
                foreach (var item in turn.GetCommonResponse().home_info.command_info_array)
                    if (EventLoggerPlugin.GameGlobal.ToTrainId.TryGetValue(item.command_id, out var value) && value == trainId)
                        foreach (var trainParam in item.params_inc_dec_info_array)
                            trainParams[trainParam.target_type] += trainParam.value;
                foreach (var item in turn.GetCommonResponse().sport_data_set.command_info_array)
                    if (EventLoggerPlugin.GameGlobal.ToTrainId.TryGetValue(item.command_id, out var value) && value == trainId)
                        foreach (var trainParam in item.params_inc_dec_info_array)
                            trainParams[trainParam.target_type] += trainParam.value;

                var stats = new TrainStats
                {
                    FailureRate = trainItems[trainId].failure_rate,
                    VitalGain = trainParams[10]
                };
                if (turn.Vital + stats.VitalGain > turn.MaxVital)
                    stats.VitalGain = turn.MaxVital - turn.Vital;
                if (stats.VitalGain < -turn.Vital)
                    stats.VitalGain = -turn.Vital;
                stats.FiveValueGain = [trainParams[1], trainParams[2], trainParams[3], trainParams[4], trainParams[5]];
                for (var j = 0; j < 5; j++)
                    stats.FiveValueGain[j] = ScoreUtils.ReviseOver1200(turn.Stats[j] + stats.FiveValueGain[j]) - ScoreUtils.ReviseOver1200(turn.Stats[j]);
                stats.PtGain = trainParams[30];
                trainStats[i] = stats;
            }

            var failureRateStr = new string[5];
            for (var i = 0; i < 5; i++)
            {
                var thisFailureRate = failureRate[EventLoggerPlugin.GameGlobal.TrainIds[i]];
                failureRateStr[i] = thisFailureRate switch
                {
                    >= 40 => $" ⚠ {thisFailureRate}%",
                    >= 20 => $" ! {thisFailureRate}%",
                    > 0 => $" ({thisFailureRate}%)",
                    _ => string.Empty
                };
            }
            var 友人在的训练 = turn.CommandInfoArray.FirstOrDefault(x => x.TrainingPartners.Any(y => y.CardId == 30188 || y.CardId == 10104));
            if (友人在的训练 != default)
            {
                turnStat.uaf_friendAtTrain[友人在的训练.TrainIndex - 1] = true;
            }
            var maxScore = trainStats.Max(x => x.FiveValueGain.Sum());
            var trainingSections = turn.CommandInfoArray.Select(command =>
            {
                var trainName = command.TrainIndex switch
                {
                    1 => $"{I18N_Speed}{failureRateStr[0]}",
                    2 => $"{I18N_Stamina}{failureRateStr[1]}",
                    3 => $"{I18N_Power}{failureRateStr[2]}",
                    4 => $"{I18N_Nuts}{failureRateStr[3]}",
                    5 => $"{I18N_Wiz}{failureRateStr[4]}",
                    _ => throw new InvalidDataException($"未知训练索引: {command.TrainIndex}")
                };

                var currentStat = turn.StatsRevised[command.TrainIndex - 1];
                var statUpToMax = turn.MaxStatsRevised[command.TrainIndex - 1] - currentStat;
                var afterVital = trainStats[command.TrainIndex - 1].VitalGain + turn.Vital;
                var stats = trainStats[command.TrainIndex - 1];
                var score = stats.FiveValueGain.Sum();
                var fever = command.Color switch
                {
                    SportColor.Blue => (turn.BlueLevel % 50 + command.TotalGainRank) >= 50,
                    SportColor.Red => (turn.RedLevel % 50 + command.TotalGainRank) >= 50,
                    SportColor.Yellow => (turn.YellowLevel % 50 + command.TotalGainRank) >= 50,
                    _ => throw new InvalidDataException($"未知 UAF 颜色: {command.Color}")
                };
                var rows = new List<string>
                {
                    $"[{SportColorText(command.Color)}] {trainName}",
                    $"{I18N_CurrentRemainStat}: {currentStat}:{statUpToMax}",
                    $"{I18N_Vital}: {afterVital}/{turn.MaxVital}",
                    $"Lv{command.TrainLevel} | SR{command.SportRank}",
                    $"{(score == maxScore ? "★ " : string.Empty)}{I18N_StatSimple}:{score} | Pt:{stats.PtGain}",
                    $"{(fever ? "★ " : string.Empty)}{I18N_RankGain}:{command.TotalGainRank}"
                };

                foreach (var trainingPartner in command.TrainingPartners)
                    rows.Add($"  {trainingPartner.Name}");

                return string.Join(Environment.NewLine, rows);
            });
            if (turn.IsRankGainIncreased)
                critInfos.Add(I18N_RankGainIncreased);

            if (turn.AvailableTalkCount > 0)
            {
                if (turn.Turn % 12 >= 9)
                    critInfos.Add(I18N_RememberUseTalk);
                // 每种颜色按最大获得Rank排序
                var groupByColorOrderByRank = turn.CommandInfoArray
                    .GroupBy(x => x.Color)
                    .OrderByDescending(x => x.Sum(y => y.GainRank));
                foreach (var i in groupByColorOrderByRank.Where(x => x.Key != SportColor.Blue)) // Color,ParsedCommandInfo
                {
                    var totalRank = turn.CommandInfoArray.Where(x => x.Color == SportColor.Blue).FirstOrDefault()?.TotalGainRank ?? 0;
                    foreach (var j in i)
                    {
                        var changed = turn.TrainingArray.First(x => x.CommandId == int.Parse($"210{j.CommandId % 10}"));
                        if (changed.SportRank + j.ActualGainRank >= 100)
                            totalRank += 100 - changed.SportRank;
                        else
                            totalRank += j.ActualGainRank;
                    }
                    if ((turn.BlueLevel % 50) + totalRank >= 50)
                    {
                        extInfos.Add(string.Format(I18N_TalkToGetBlueBuff, SportColorText(i.Key)));
                    }
                }
                foreach (var i in groupByColorOrderByRank.Where(x => x.Key != SportColor.Red)) // Color,ParsedCommandInfo
                {
                    var totalRank = turn.CommandInfoArray.Where(x => x.Color == SportColor.Red).FirstOrDefault()?.TotalGainRank ?? 0;
                    foreach (var j in i)
                    {
                        var changed = turn.TrainingArray.First(x => x.CommandId == int.Parse($"220{j.CommandId % 10}"));
                        if (changed.SportRank + j.ActualGainRank >= 100)
                            totalRank += 100 - changed.SportRank;
                        else
                            totalRank += j.ActualGainRank;
                    }
                    if ((turn.RedLevel % 50) + totalRank >= 50)
                    {
                        extInfos.Add(string.Format(I18N_TalkToGetRedBuff, SportColorText(i.Key)));
                    }
                }
                foreach (var i in groupByColorOrderByRank.Where(x => x.Key != SportColor.Yellow)) // Color,ParsedCommandInfo
                {
                    var totalRank = turn.CommandInfoArray.Where(x => x.Color == SportColor.Yellow).FirstOrDefault()?.TotalGainRank ?? 0;
                    foreach (var j in i)
                    {
                        var changed = turn.TrainingArray.First(x => x.CommandId == int.Parse($"230{j.CommandId % 10}"));
                        if (changed.SportRank + j.ActualGainRank >= 100)
                            totalRank += 100 - changed.SportRank;
                        else
                            totalRank += j.ActualGainRank;
                    }
                    if ((turn.YellowLevel % 50) + totalRank >= 50)
                    {
                        extInfos.Add(string.Format(I18N_TalkToGetYellowBuff, SportColorText(i.Key)));
                    }
                }
            }
            var nextRank = turn.Turn switch
            {
                <= 12 => 0,
                <= 24 => 10,
                <= 36 => 20,
                <= 48 => 30,
                <= 60 => 40,
                _ => 50
            };
            var lowRankSports = turn.TrainingArray.Where(x => x.SportRank < nextRank);
            if (lowRankSports.Any())
            {
                extInfos.Add(string.Format(I18N_MinimumSportRank, nextRank));
                foreach (var i in lowRankSports)
                {
                    extInfos.Add(string.Format(
                        I18N_LowSportRank,
                        $"{SportColorText(i.Color)} {GGlobal.TrainNames[GameGlobal.ToTrainId[i.CommandId]]}",
                        nextRank - i.SportRank));
                }

                extInfos.Add(string.Empty);
            }
            else if (nextRank > 0)
            {
                extInfos.Add(I18N_AllRankOK);
                extInfos.Add(I18N_LowestSportRank);
                var minRank = turn.TrainingArray.Min(x => x.SportRank);
                var lowestSports = turn.TrainingArray.Where(x => x.SportRank == minRank).Select(
                    x => $"{SportColorText(x.Color)} {GGlobal.TrainNames[GameGlobal.ToTrainId[x.CommandId]]}: Lv{x.SportRank}");
                foreach (var line in lowestSports)
                    extInfos.Add(line);
                extInfos.Add(string.Empty);
            }

            EventLogger.CommitScenarioTurn(
                @event.data.chara_info.scenario_id,
                turn.Turn,
                turnStat);
            round = EventLogger.Current;

            //友人点了几次，来了几次
            var friendClickedTimes = 0;
            var friendChargedTimes = 0; //友人冲了几次体力
            for (var t = turn.Turn; t >= 1; t--)
            {
                if (round.Turns[t] is not { } stats) break;
                if (!GameGlobal.TrainIds.Any(x => x == stats.PlayerChoice)) //没训练
                    continue;
                if (stats.IsTrainingFailed)//训练失败
                    continue;
                if (!stats.UafFriendAtTrain[GameGlobal.ToTrainIndex[stats.PlayerChoice]])
                    continue;//没点友人
                if (stats.UafFriendEvent == 5)//启动事件
                    continue;//没点佐岳
                friendClickedTimes += 1;
                if (stats.UafFriendEvent is 1 or 2)
                    friendChargedTimes += 1;
            }

            // 计算友人表现（分位数）
            if (friendClickedTimes > 1)
            {
                var p = 0.4;
                //(p(n<=k-1) + p(n<=k)) / 2
                var bn = Binomial.CDF(p, friendClickedTimes, friendChargedTimes);
                var bn_1 = Binomial.CDF(p, friendClickedTimes, friendChargedTimes - 1);
                extInfos.Add(string.Format(I18N_MoritaTrained.Trim(), friendClickedTimes));
                extInfos.Add(string.Format(I18N_MoritaVitalGainTimes.Trim(), friendChargedTimes));
                extInfos.Add(string.Format(I18N_MoritaRanking, ((bn + bn_1) / 2 * 100).ToString("0")));
            }

            // 计算连续事件表现
            var eventPerf = EventLogger.PrintCardEventPerf(@event.data.chara_info.scenario_id);
            if (eventPerf.Count > 0)
            {
                extInfos.AddRange(eventPerf);
            }

            var motivation = @event.data.chara_info.motivation switch
            {
                5 => I18N_MotivationBest,
                4 => I18N_MotivationGood,
                3 => I18N_MotivationNormal,
                2 => I18N_MotivationBad,
                1 => I18N_MotivationWorst,
                _ => throw new InvalidDataException($"未知干劲值: {@event.data.chara_info.motivation}")
            };
            var lines = new List<string>
            {
                $"{turn.Year}{I18N_Year} {turn.Month}{I18N_Month}{turn.HalfMonth} | 总属性: {totalValue} | {I18N_Vital}: {turn.Vital}/{turn.MaxVital} | {motivation}"
            };
            if (critInfos.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("== 重要信息 ==");
                lines.AddRange(critInfos);
            }
            lines.Add(string.Empty);
            lines.Add("== 训练信息 ==");
            lines.AddRange(trainingSections.SelectMany(x => new[] { x, string.Empty }));
            if (extInfos.Any(x => !string.IsNullOrWhiteSpace(x)))
            {
                lines.Add("== Extras ==");
                lines.AddRange(extInfos);
            }

            return WorkspaceContent.Text(string.Join(Environment.NewLine, lines));

            static string SportColorText(SportColor color) =>
                color switch
                {
                    SportColor.Blue => I18N_Blue,
                    SportColor.Red => I18N_Red,
                    SportColor.Yellow => I18N_Yellow,
                    _ => throw new InvalidDataException($"未知 UAF 颜色: {color}")
                };
        }

    }
}
