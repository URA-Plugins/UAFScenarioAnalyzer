using EventLoggerPlugin;
using Gallop;
using MathNet.Numerics.Distributions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Spectre.Console;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Threading.Tasks;
using UmamusumeResponseAnalyzer;
using UmamusumeResponseAnalyzer.Game.TurnInfo;
using UmamusumeResponseAnalyzer.LocalizedLayout.Handlers;
using UmamusumeResponseAnalyzer.Plugin;
using static UAFScenarioAnalyzer.i18n.Game;
using static UAFScenarioAnalyzer.i18n.UAF;
using static UmamusumeResponseAnalyzer.Game.TurnInfo.TurnInfoUAF;

namespace UAFScenarioAnalyzer
{
    public class UAFScenarioAnalyzer : IPlugin
    {
        public Version Version => new(1, 0, 0);

        public string Name => "UAFScenarioAnalyzer";

        public string Author => "";
        public async Task UpdatePlugin(ProgressContext ctx)
        {
            var progress = ctx.AddTask($"[UAFScenarioAnalyzer] Update");

            using var client = new HttpClient();
            using var resp = await client.GetAsync("https://api.github.com/repos/URA-Plugins/UAFScenarioAnalyzer/releases/latest");
            var json = await resp.Content.ReadAsStringAsync();
            var jo = JObject.Parse(json);

            var isLatest = ("v" + Version.ToString()).Equals("v" + jo["tag_name"]?.ToString());
            if (isLatest)
            {
                progress.Increment(progress.MaxValue);
                progress.StopTask();
                return;
            }
            progress.Increment(25);

            using var msg = await client.GetAsync(jo["assets"][0]["browser_download_url"].ToString(), HttpCompletionOption.ResponseHeadersRead);
            using var stream = await msg.Content.ReadAsStreamAsync();
            var buffer = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                    break;
                progress.Increment(read / msg.Content.Headers.ContentLength ?? 1 * 0.5);
            }
            using var archive = new ZipArchive(stream);
            archive.ExtractToDirectory("Plugins", true);
            progress.Increment(25);

            progress.StopTask();
        }
        [Analyzer(priority: 1)]
        public static void Analyze(JObject jo)
        {
            if (!jo.HasCharaInfo()) return;
            if (jo["data"] is null || jo["data"] is not JObject data) return;
            if (data["chara_info"] is null || data["chara_info"] is not JObject chara_info) return;
            if (chara_info["scenario_id"].ToInt() != (int)ScenarioType.UAF) return;
            var state = chara_info["state"].ToInt();
            if (chara_info != null && data["home_info"]?["command_info_array"] != null && data["race_reward_info"].IsNull() && !(state is 2 or 3)) //根据文本简单过滤防止重复、异常输出
            {
                var @event = jo.ToObject<Gallop.SingleModeCheckEventResponse>();
                if ((@event.data.unchecked_event_array != null && @event.data.unchecked_event_array.Length > 0) || @event.data.race_start_info != null) return;
                ParseSportCommandInfo(@event);
            }
        }
        public static void ParseSportCommandInfo(Gallop.SingleModeCheckEventResponse @event)
        {
            if ((@event.data.unchecked_event_array != null && @event.data.unchecked_event_array.Length > 0) || @event.data.race_start_info != null) return;
            var layout = new Layout().SplitColumns(
                new Layout("Main").Size(CommandInfoLayout.Current.MainSectionWidth).SplitRows(
                    new Layout("体力干劲条").SplitColumns(
                        new Layout("日期").Ratio(4),
                        new Layout("总属性").Ratio(4),
                        new Layout("体力").Ratio(8),
                        new Layout("干劲").Ratio(2)).Size(3),
                    new Layout("重要信息").Size(5),
                    new Layout("分割", new Rule()).Size(1),
                    new Layout("训练信息")  // size 20, 共约30行
                    ).Ratio(4),
                new Layout("Ext").Ratio(1)
                );
            var extInfos = new List<string>();
            var critInfos = new List<string>();
            var turn = new TurnInfoUAF(@event.data);

            if (GameStats.currentTurn != turn.Turn - 1 //正常情况
                && GameStats.currentTurn != turn.Turn //重复显示
                && turn.Turn != 1 //第一个回合
                )
            {
                GameStats.isFullGame = false;
                critInfos.Add(string.Format(I18N_WrongTurnAlert, GameStats.currentTurn, turn.Turn));
                GameStats.currentTurn = turn.Turn;
                EventLogger.Init(@event);
            }
            else if (turn.Turn == 1)
            {
                GameStats.isFullGame = true;
                EventLogger.Init(@event);
            }

            //买技能，大师杯剧本年末比赛，会重复显示
            if (@event.data.chara_info.playing_state != 1 || GameStats.currentTurn == turn.Turn)
            {
                critInfos.Add(I18N_RepeatTurn);
                if (GameStats.stats[turn.Turn] == null)
                {
                    // FIXME: 中途开始回合时可能会到这个分支。问题还需要排查
                    critInfos.Add("[red]中途开始回合[/]");
                    GameStats.stats[turn.Turn] = new TurnStats();
                }
            }
            else
            {
                //初始化TurnStats
                GameStats.whichScenario = @event.data.chara_info.scenario_id;
                GameStats.stats[turn.Turn] = new TurnStats();
                EventLogger.Update(@event);
            }
            GameStats.currentTurn = turn.Turn;
            var blueFever = false;
            var redFever = false;
            var yellowFever = false;

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
            var fiveValueMaxRevised = new int[]
            {
                ScoreUtils.ReviseOver1200(@event.data.chara_info.max_speed),
                ScoreUtils.ReviseOver1200(@event.data.chara_info.max_stamina),
                ScoreUtils.ReviseOver1200(@event.data.chara_info.max_power) ,
                ScoreUtils.ReviseOver1200(@event.data.chara_info.max_guts) ,
                ScoreUtils.ReviseOver1200(@event.data.chara_info.max_wiz) ,
            };
            var currentFiveValueRevised = currentFiveValue.Select(x => ScoreUtils.ReviseOver1200(x)).ToArray();
            var totalValue = currentFiveValueRevised.Sum();
            var totalValueWithPt = totalValue + @event.data.chara_info.skill_point;
            // var totalValueWithHalfPt = totalValue + 0.5 * @event.data.chara_info.skill_point;

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

            var grids = new Grid();
            grids.AddColumns(5);
            var maxColor = turn.CommandInfoArray.GroupBy(x => x.Color).MaxBy(x => x.Count())?.Key;

            var failureRateStr = new string[5];
            //失败率>=40%标红、>=20%(有可能大失败)标DarkOrange、>0%标黄
            for (var i = 0; i < 5; i++)
            {
                var thisFailureRate = failureRate[EventLoggerPlugin.GameGlobal.TrainIds[i]];
                failureRateStr[i] = thisFailureRate switch
                {
                    >= 40 => $"[red]({thisFailureRate}%)[/]",
                    >= 20 => $"[darkorange]({thisFailureRate}%)[/]",
                    > 0 => $"[yellow]({thisFailureRate}%)[/]",
                    _ => string.Empty
                };
            }
            var 友人在的训练 = turn.CommandInfoArray.FirstOrDefault(x => x.TrainingPartners.Any(y => y.CardId == 30188 || y.CardId == 10104));
            if (友人在的训练 != default)
            {
                GameStats.stats[turn.Turn].uaf_friendAtTrain[友人在的训练.TrainIndex - 1] = true;
            }
            var commands = turn.CommandInfoArray.Select(command =>
            {
                var table = new Table()
                .AddColumn(command.TrainIndex switch
                {
                    1 => $"{I18N_Speed}{failureRateStr[0]}",
                    2 => $"{I18N_Stamina}{failureRateStr[1]}",
                    3 => $"{I18N_Power}{failureRateStr[2]}",
                    4 => $"{I18N_Nuts}{failureRateStr[3]}",
                    5 => $"{I18N_Wiz}{failureRateStr[4]}"
                })
                .BorderColor(command.Color switch
                {
                    SportColor.Blue => Color.Blue,
                    SportColor.Red => Color.Red,
                    SportColor.Yellow => Color.Yellow,
                    _ => throw new NotImplementedException(),
                });

                var currentStat = turn.StatsRevised[command.TrainIndex - 1];
                var statUpToMax = turn.MaxStatsRevised[command.TrainIndex - 1] - currentStat;
                table.AddRow(I18N_CurrentRemainStat);
                table.AddRow($"{currentStat}:{statUpToMax switch
                {
                    > 400 => $"{statUpToMax}",
                    > 200 => $"[yellow]{statUpToMax}[/]",
                    _ => $"[red]{statUpToMax}[/]"
                }}");
                table.AddRow(new Rule());

                var afterVital = trainStats[command.TrainIndex - 1].VitalGain + turn.Vital;
                table.AddRow(afterVital switch
                {
                    < 30 => $"{I18N_Vital}:[red]{afterVital}[/]/{turn.MaxVital}",
                    < 50 => $"{I18N_Vital}:[darkorange]{afterVital}[/]/{turn.MaxVital}",
                    < 70 => $"{I18N_Vital}:[yellow]{afterVital}[/]/{turn.MaxVital}",
                    _ => $"{I18N_Vital}:[green]{afterVital}[/]/{turn.MaxVital}"
                });
                table.AddRow($"Lv{command.TrainLevel} | SR{command.SportRank}");
                table.AddRow(new Rule());

                var stats = trainStats[command.TrainIndex - 1];
                var score = stats.FiveValueGain.Sum();
                if (score == trainStats.Max(x => x.FiveValueGain.Sum()))
                    table.AddRow($"{I18N_StatSimple}:[aqua]{score}[/]|Pt:{stats.PtGain}");
                else
                    table.AddRow($"{I18N_StatSimple}:{score}|Pt:{stats.PtGain}");

                blueFever = (turn.BlueLevel % 50 + command.TotalGainRank) >= 50;
                redFever = (turn.RedLevel % 50 + command.TotalGainRank) >= 50;
                yellowFever = (turn.YellowLevel % 50 + command.TotalGainRank) >= 50;
                switch (command.Color)
                {
                    case SportColor.Blue:
                        table.AddRow(blueFever ? $"[blue]{I18N_RankGain}:{command.TotalGainRank}[/]" : $"{I18N_RankGain}:{command.TotalGainRank}");
                        break;
                    case SportColor.Red:
                        table.AddRow(redFever ? $"[red]{I18N_RankGain}:{command.TotalGainRank}[/]" : $"{I18N_RankGain}:{command.TotalGainRank}");
                        break;
                    case SportColor.Yellow:
                        table.AddRow(yellowFever ? $"[yellow]{I18N_RankGain}:{command.TotalGainRank}[/]" : $"{I18N_RankGain}:{command.TotalGainRank}");
                        break;
                }
                table.AddRow(new Rule());

                foreach (var trainingPartner in command.TrainingPartners)
                {
                    table.AddRow(trainingPartner.Name);
                }
                for (var i = 5 - command.TrainingPartners.Count(); i > 0; i--)
                {
                    table.AddRow(string.Empty);
                }

                return new Padder(table).Padding(0, command.Color == maxColor ? 0 : 1, 0, 0);
            });
            grids.AddRow([.. commands]);
            layout["训练信息"].Update(grids);
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
                        extInfos.Add(string.Format(I18N_TalkToGetBlueBuff, ColorToMarkup(i.Key)));
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
                        extInfos.Add(string.Format(I18N_TalkToGetRedBuff, ColorToMarkup(i.Key)));
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
                        extInfos.Add(string.Format(I18N_TalkToGetYellowBuff, ColorToMarkup(i.Key)));
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
                    extInfos.Add(string.Format(I18N_LowSportRank, ColorToMarkup(i.Color, GGlobal.TrainNames[GameGlobal.ToTrainId[i.CommandId]]), nextRank - i.SportRank));
                }

                extInfos.Add(string.Empty);

                if (turn.AvailableTalkCount > 0)
                {
                    // 按照缺少等级的训练数量由多到少排列
                    var groupByColor = lowRankSports.GroupBy(x => x.Color).OrderByDescending(x => x.Count());
                    var requireRankGroup = groupByColor.FirstOrDefault();
                    if (requireRankGroup != null)
                    {
                        var requireRankColor = requireRankGroup.Key;
                        var requireSportCmd = requireRankGroup.Select(x => x.TrainIndex);
                        // 仅在有至少两种颜色都不够等级时才显示
                        if (requireRankGroup.Count() >= 2)
                        {
                            var otherColorCommands = turn.CommandInfoArray.Where(x => x.Color != requireRankColor && requireSportCmd.Contains(x.TrainIndex));
                        }
                    }
                    var currentCommandIds = turn.CommandInfoArray.Select(x => x.CommandId).Where(x => lowRankSports.Contains(y => y.CommandId == x));
                    var availableSportTrains = turn.CommandInfoArray.Where(x => currentCommandIds.Contains(x.CommandId));

                    extInfos.Add(string.Empty);
                }
            }
            else if (nextRank > 0)
            {
                extInfos.Add(string.Format(I18N_AllRankOK));
                extInfos.Add(string.Format(I18N_LowestSportRank));
                var minRank = turn.TrainingArray.Min(x => x.SportRank);
                var lowestSports = turn.TrainingArray.Where(x => x.SportRank == minRank).Select(
                    x => $"{ColorToMarkup(x.Color)} {GGlobal.TrainNames[GameGlobal.ToTrainId[x.CommandId]]}: Lv{x.SportRank}");
                foreach (var line in lowestSports)
                    extInfos.Add(string.Format(line));
                extInfos.Add(string.Empty);
            }

            //友人点了几次，来了几次
            var friendClickedTimes = 0;
            var friendChargedTimes = 0; //友人冲了几次体力
            for (var t = turn.Turn; t >= 1; t--)
            {
                if (GameStats.stats[t] == null) break;
                if (!GameGlobal.TrainIds.Any(x => x == GameStats.stats[t].playerChoice)) //没训练
                    continue;
                if (GameStats.stats[t].isTrainingFailed)//训练失败
                    continue;
                if (!GameStats.stats[t].uaf_friendAtTrain[GameGlobal.ToTrainIndex[GameStats.stats[t].playerChoice]])
                    continue;//没点友人
                if (GameStats.stats[t].uaf_friendEvent == 5)//启动事件
                    continue;//没点佐岳
                friendClickedTimes += 1;
                if (GameStats.stats[t].uaf_friendEvent == 1 || GameStats.stats[t].uaf_friendEvent == 2)
                    friendChargedTimes += 1;
            }

            // 计算友人表现（分位数）
            var friendPerformance = String.Empty;
            if (friendClickedTimes > 1)
            {
                var p = 0.4;
                //(p(n<=k-1) + p(n<=k)) / 2
                var bn = Binomial.CDF(p, friendClickedTimes, friendChargedTimes);
                var bn_1 = Binomial.CDF(p, friendClickedTimes, friendChargedTimes - 1);
                friendPerformance = string.Format(I18N_MoritaRanking, ((bn + bn_1) / 2 * 100).ToString("0"));
                extInfos.Add(string.Format(I18N_MoritaTrained.Trim(), friendClickedTimes));
                extInfos.Add(string.Format(I18N_MoritaVitalGainTimes.Trim(), friendChargedTimes));
                extInfos.Add(friendPerformance);
            }

            // 计算连续事件表现
            var eventPerf = EventLogger.PrintCardEventPerf(@event.data.chara_info.scenario_id);
            if (eventPerf.Count > 0)
            {
                extInfos.AddRange(eventPerf);
            }

            layout["日期"].Update(new Panel($"{turn.Year}{I18N_Year} {turn.Month}{I18N_Month}{turn.HalfMonth}").Expand());
            layout["总属性"].Update(new Panel($"总属性: {totalValue}").Expand());
            layout["体力"].Update(new Panel($"{I18N_Vital}: [green]{turn.Vital}[/]/{turn.MaxVital}").Expand());
            layout["干劲"].Update(new Panel(@event.data.chara_info.motivation switch
            {
                5 => $"[green]{I18N_MotivationBest}[/]",
                4 => $"[yellow]{I18N_MotivationGood}[/]",
                3 => $"[red]{I18N_MotivationNormal}[/]",
                2 => $"[red]{I18N_MotivationBad}[/]",
                1 => $"[red]{I18N_MotivationWorst}[/]"
            }).Expand());
            layout["重要信息"].Update(new Panel(string.Join(Environment.NewLine, critInfos)).Expand());
            layout["Ext"].Update(new Panel(string.Join(Environment.NewLine, extInfos)));
            AnsiConsole.Write(layout);
            // 光标倒转一点
            AnsiConsole.Cursor.MoveUp(AnsiConsole.Console.Profile.Height - 30);
            AnsiConsole.Cursor.MoveLeft(AnsiConsole.Console.Profile.Width);

            static string ColorToMarkup(SportColor c, string? text = null!) =>
                c switch
                {
                    SportColor.Blue => $"[blue]{text ?? I18N_Blue}[/]",
                    SportColor.Red => $"[red]{text ?? I18N_Red}[/]",
                    SportColor.Yellow => $"[yellow]{text ?? I18N_Yellow}[/]",
                    _ => throw new NotImplementedException(),
                };
        }
    }
}
