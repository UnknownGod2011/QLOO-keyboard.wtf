using KeyboardWtf.Services;
using KeyboardWtf.Services.BrowserAutomationV2;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class BrowserFormContextTests
{
    private static BrowserControl Choice(string id, string field, string label, bool selected) => new()
    {
        Id = id, FieldId = field, FieldLabel = label, FieldRequired = true,
        FieldAnswered = selected, Role = "radio", AccessibleName = "4",
        Checked = selected, Visible = true, Enabled = true,
    };

    [Fact]
    public void Repeated_choice_names_require_unique_field_grounding()
    {
        var first = Choice("g1c1", "usefulness", "Usefulness", true);
        var second = Choice("g1c2", "quality", "Quality", false);
        var page = new BrowserPageSnapshot { Controls = [first, second] };
        var action = new BrowserPlannerAction { Type = "click", TargetRole = "radio", TargetName = "4" };
        Assert.Null(BrowserAutomationCoordinator.FindMatchingControl(page, action));
        action.TargetFieldId = "quality";
        Assert.Same(second, BrowserAutomationCoordinator.FindMatchingControl(page, action));
        action.TargetId = "g1c1";
        Assert.Null(BrowserAutomationCoordinator.FindMatchingControl(page, action));
    }

    [Fact]
    public void Completion_cannot_accept_a_rating_in_the_wrong_required_question()
    {
        var page = new BrowserPageSnapshot
        {
            Controls = [Choice("new1", "usefulness", "Usefulness", true), Choice("new2", "quality", "Quality", false)],
        };
        var receipt = new BrowserActionReceipt
        {
            Ok = true, Action = "click", TargetName = "4", TargetRole = "radio",
            TargetFieldId = "usefulness", CheckedChanged = true,
        };
        var result = new BrowserAutomationVerifier().VerifyTerminal(
            "Fill the survey and skip unknown optional fields. Do not submit.",
            new BrowserPlannerDecision { Done = true }, page, [receipt]);
        Assert.False(result.Verified);
        Assert.Contains("Quality", result.Error);
        Assert.Contains("required", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fill_the_current_form_rejects_an_unanswered_required_rating()
    {
        var answered = Choice("answer", "earlier", "Earlier rating", true);
        var missing = Choice("missing", "stability", "Overall performance and stability", false);
        var result = new BrowserAutomationVerifier().VerifyTerminal(
            "Use the copied text to fill the current form.",
            new BrowserPlannerDecision { Done = true, CompletionChecks = [new() {
                Type = "check", TargetFieldId = "earlier", TargetRole = "radio", TargetName = "4" }] },
            new BrowserPageSnapshot { Controls = [answered, missing] },
            []);

        Assert.False(result.Verified);
        Assert.Contains("Overall performance and stability", result.Error);
        Assert.Contains("required", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Negated_whole_form_request_keeps_a_targeted_field_edit_targeted()
    {
        var name = new BrowserControl
        {
            Id = "name", FieldId = "name", FieldLabel = "Display name", Role = "textbox",
            AccessibleName = "Display name", Value = "Ada", Visible = true, Enabled = true,
            FieldRequired = true, FieldAnswered = true,
        };
        var unrelated = Choice("rating", "stability", "Overall stability", false);
        const string request = "Do not fill the current form; set only Display name to Ada.";
        Assert.False(BrowserAutomationVerifier.RequestsWholeFormMutation(request));
        Assert.False(BrowserAutomationVerifier.RequestsWholeFormMutation(
            "Do not fill all fields; change only question one."));
        Assert.True(BrowserAutomationVerifier.RequestsWholeFormMutation(
            "Fill the current form, but do not submit it."));

        var result = new BrowserAutomationVerifier().VerifyTerminal(
            request,
            new BrowserPlannerDecision { Done = true, CompletionChecks = [new() {
                Type = "type", TargetFieldId = "name", TargetRole = "textbox",
                TargetName = "Display name", Text = "Ada" }] },
            new BrowserPageSnapshot { Controls = [name, unrelated] },
            []);

        Assert.True(result.Verified, result.Error);
    }

    [Fact]
    public void Latest_explicit_scope_narrowing_overrides_an_earlier_whole_form_goal()
    {
        var narrowed = BrowserAutomationCoordinator.BuildCorrectedTaskRequest(
            "Fill the current form using the provided context. Do not submit.",
            "Do not fill the current form; only set Display name to Ada.");
        Assert.False(BrowserAutomationVerifier.RequestsWholeFormMutation(narrowed));

        var name = new BrowserControl
        {
            Id = "name", FieldId = "name", FieldLabel = "Display name", Role = "textbox",
            AccessibleName = "Display name", Value = "Ada", Visible = true, Enabled = true,
            FieldRequired = true, FieldAnswered = true,
        };
        var page = new BrowserPageSnapshot
        {
            Controls = [name, Choice("rating", "stability", "Overall stability", false)],
        };
        var decision = new BrowserPlannerDecision { Done = true, CompletionChecks = [new()
        {
            Type = "type", TargetFieldId = "name", TargetRole = "textbox",
            TargetName = "Display name", Text = "Ada",
        }] };
        var narrowedResult = new BrowserAutomationVerifier().VerifyTerminal(
            narrowed, decision, page, []);
        Assert.True(narrowedResult.Verified, narrowedResult.Error);

        var ordinaryCorrection = BrowserAutomationCoordinator.BuildCorrectedTaskRequest(
            "Fill the current form using the provided context. Do not submit.",
            "For question one, select macOS instead of Windows 11.");
        Assert.True(BrowserAutomationVerifier.RequestsWholeFormMutation(ordinaryCorrection));
        var ordinaryResult = new BrowserAutomationVerifier().VerifyTerminal(
            ordinaryCorrection, decision, page, []);
        Assert.False(ordinaryResult.Verified);
        Assert.Contains("Overall stability", ordinaryResult.Error);

        var stillNarrowed = BrowserAutomationCoordinator.BuildCorrectedTaskRequest(
            narrowed, "Change Display name to Ada instead.");
        Assert.False(BrowserAutomationVerifier.RequestsWholeFormMutation(stillNarrowed));
        var restored = BrowserAutomationCoordinator.BuildCorrectedTaskRequest(
            stillNarrowed, "Fill the whole form after all.");
        Assert.True(BrowserAutomationVerifier.RequestsWholeFormMutation(restored));

        var explicitFields = BrowserAutomationCoordinator.BuildCorrectedTaskRequest(
            "Fill the form: set Display name to Ada and set Overall stability to 4.",
            "Do not fill the form; only change Display name to Ada.");
        var explicitResult = new BrowserAutomationVerifier().VerifyTerminal(
            explicitFields, decision, page, []);
        Assert.True(explicitResult.Verified, explicitResult.Error);
        Assert.Equal(1, BrowserAutomationVerifier.CountRequestedFormMutationSteps(explicitFields));

        var sourceMention = CommandRegistry.BuildBrowserTaskWithCapturedContext(
            "Set Display name to Ada.",
            "clipboard text",
            "A page note says to fill the whole form later. Then set Q2 to something.");
        Assert.False(BrowserAutomationVerifier.RequestsWholeFormMutation(sourceMention));
        Assert.Equal(1, BrowserAutomationVerifier.CountRequestedFormMutationSteps(sourceMention));
    }

    [Fact]
    public void Optional_empty_fields_do_not_block_complete_required_answers()
    {
        var answer = Choice("new1", "quality", "Quality", true);
        var optional = Choice("new2", "optional", "Optional detail", false);
        optional.FieldRequired = false;
        var page = new BrowserPageSnapshot { Controls = [answer, optional] };
        var receipt = new BrowserActionReceipt
        {
            Ok = true, Action = "click", TargetName = "4", TargetRole = "radio",
            TargetId = "old1", TargetFieldId = "quality", CheckedChanged = true,
        };
        var verifier = new BrowserAutomationVerifier();
        var decision = new BrowserPlannerDecision { Done = true, CompletionChecks = [
            new() { Type = "check", TargetFieldId = "quality", TargetRole = "radio", TargetName = "4" }] };
        Assert.True(verifier.VerifyTerminal("Fill the survey", decision, page, [receipt]).Verified);
        receipt.TargetFieldId = "optional";
        Assert.False(verifier.VerifyTerminal("Fill the survey", decision, page, [receipt]).Verified);
    }

    [Fact]
    public void Planner_receives_question_identity_and_actual_required_state()
    {
        var control = Choice("g1c1", "quality", "Quality", false);
        var prompt = BrowserAutomationPlanner.BuildPrompt("Fill the survey", new(), [], [control]);
        Assert.Contains("fieldId=\"quality\"", prompt);
        Assert.Contains("field=\"Quality\" required=True answered=False", prompt);
        Assert.Contains("action.targetFieldId", prompt);
    }

    [Fact]
    public void Targeted_field_edit_does_not_expand_into_whole_form_completion()
    {
        var requested = Choice("name", "name-field", "Full name", true);
        requested.Role = "textbox";
        requested.AccessibleName = "Full name";
        requested.Value = "Handoff User";
        var unrelatedRequired = Choice("email", "email-field", "Email address", false);
        unrelatedRequired.Role = "textbox";
        unrelatedRequired.AccessibleName = "Email address";
        var page = new BrowserPageSnapshot { Controls = [requested, unrelatedRequired] };
        var receipt = new BrowserActionReceipt
        {
            Ok = true,
            Action = "type",
            TargetName = "Full name",
            TargetRole = "textbox",
            TargetFieldId = "name-field",
            ValueMatches = true,
            ExpectedValue = "Handoff User",
        };

        var result = new BrowserAutomationVerifier().VerifyTerminal(
            "Fill the Full name field with Handoff User on the current form.",
            new BrowserPlannerDecision { Done = true, CompletionChecks = [new() {
                Type = "type", TargetFieldId = "name-field", TargetRole = "textbox", TargetName = "Full name", Text = "Handoff User" }] },
            page,
            [receipt]);

        Assert.True(result.Verified);
        var prompt = BrowserAutomationPlanner.BuildPrompt(
            "Fill the Full name field with Handoff User on the current form.",
            new(), [], [requested, unrelatedRequired]);
        Assert.Contains("editing one named field does not authorize completing the whole form", prompt);
    }
}
