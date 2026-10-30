using System.Globalization;

RepositoryChecks.Run();
CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
using var ui = new WorkspaceSmokeSession();
ScenarioMigrationSmoke.Run(ui);
ui.InvokeOnOwner(() => { HistoryConfigSmoke.Run(ui.Application); return true; });
Console.WriteLine("UAFScenarioAnalyzer smoke passed.");
