namespace DeckFlow.Web.Models;

public sealed record WorkflowStepTab(int Step, string Label, bool IsComplete);

public sealed record WorkflowStepTabsModel(
    string AriaLabel,
    string TabIdPrefix,
    string PanelIdPrefix,
    string DataShowStepAttribute,
    IReadOnlyList<WorkflowStepTab> Steps);
