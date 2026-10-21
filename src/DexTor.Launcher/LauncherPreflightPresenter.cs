using System.Collections.Generic;
using System.Linq;

using DexTor.Application;

namespace DexTor.Launcher;

public sealed record LauncherRequirementRow(
    string StateLabel,
    PreflightRequirementState State,
    string Title,
    string Description,
    string? InfoText,
    string? SolutionText,
    string? ActionText,
    string? ActionUrl);

public sealed record LauncherPreflightPresentation(
    string WindowTitle,
    string Header,
    string SubHeader,
    IReadOnlyList<LauncherRequirementRow> Requirements,
    LauncherPrimaryAction PrimaryAction,
    string PrimaryActionLabel,
    LauncherPrimaryAction? SecondaryAction,
    string? SecondaryActionLabel,
    bool CanContinue,
    bool HasActionLink,
    bool ShouldExit);

public enum LauncherPrimaryAction
{
    ContinueSetup,
    StartOverSetup,
    ConnectDexTorUsb,
    CreateDexTorUsb,
    StartDexTor,
    CheckAndRecover,
    RepairAndStart,
    OpenDexTor,
    EndSession,
    EjectUsb,
    RestartRequired,
    UpdateAndStart,
    Continue,
    Retry,
    Prepare
}

public static class LauncherPreflightPresenter
{
    public static LauncherPreflightPresentation Build(
        PreflightReport report,
        ILocalizer localizer,
        LauncherContext? context = null)
    {
        var hasWhpxWarning = report.Requirements.Any(r =>
            r.Kind == PreflightRequirementKind.HostVirtualization &&
            r.State == PreflightRequirementState.Warning);

        var hasWarnings = report.Requirements.Any(r => r.State == PreflightRequirementState.Warning);
        var canContinue = report.CanContinueWithWarnings;
        var builtRows = BuildRows(report, localizer).ToArray();
        var hasActionLink = builtRows.Any(r => !string.IsNullOrWhiteSpace(r.ActionUrl));

        var hasRuntimeContext = context is not null;
        var primaryAction = hasWhpxWarning
            ? LauncherPrimaryAction.Prepare
            : canContinue
                ? ResolvePrimaryAction(context, hasRuntimeContext)
                : LauncherPrimaryAction.Retry;
        var secondaryAction = ResolveSecondaryAction(primaryAction, context);

        if (!hasRuntimeContext)
        {
            primaryAction = canContinue && !hasWhpxWarning
                ? LauncherPrimaryAction.Continue
                : primaryAction;
        }

        var header = !canContinue
            ? localizer.T("launcher.blocked.title")
            : hasWarnings
                ? localizer.T("launcher.warning")
                : localizer.T("launcher.ready");

        var subHeader = canContinue
            ? localizer.T("launcher.ready.details")
            : localizer.T("launcher.blocked.details");

        return new LauncherPreflightPresentation(
            localizer.T("launcher.window.title"),
            header,
            subHeader,
            builtRows,
            primaryAction,
            PrimaryActionLabel(primaryAction, localizer),
            secondaryAction,
            secondaryAction is null ? null : PrimaryActionLabel(secondaryAction.Value, localizer),
            canContinue,
            hasActionLink,
            !canContinue);
    }

    private static LauncherPrimaryAction ResolvePrimaryAction(
        LauncherContext? context,
        bool hasRuntimeContext)
    {
        if (!hasRuntimeContext || context is null)
        {
            return LauncherPrimaryAction.Continue;
        }

        return PrimaryActionResolver.Resolve(context) switch
        {
            PrimaryAction.ContinueSetup => LauncherPrimaryAction.ContinueSetup,
            PrimaryAction.StartOverSetup => LauncherPrimaryAction.StartOverSetup,
            PrimaryAction.ConnectDexTorUsb => LauncherPrimaryAction.Continue,
            PrimaryAction.CreateDexTorUsb => LauncherPrimaryAction.Continue,
            PrimaryAction.StartDexTor => LauncherPrimaryAction.StartDexTor,
            PrimaryAction.PrepareThisPc => LauncherPrimaryAction.Prepare,
            PrimaryAction.RestartRequired => LauncherPrimaryAction.RestartRequired,
            PrimaryAction.UpdateAndStart => LauncherPrimaryAction.UpdateAndStart,
            PrimaryAction.OpenDexTor => LauncherPrimaryAction.OpenDexTor,
            PrimaryAction.EndSession => LauncherPrimaryAction.EndSession,
            PrimaryAction.EjectUsb => LauncherPrimaryAction.EjectUsb,
            PrimaryAction.CheckAndRecover => LauncherPrimaryAction.CheckAndRecover,
            PrimaryAction.RepairAndStart => LauncherPrimaryAction.RepairAndStart,
            _ => LauncherPrimaryAction.Continue
        };
    }

    private static LauncherPrimaryAction? ResolveSecondaryAction(
        LauncherPrimaryAction primaryAction,
        LauncherContext? context)
    {
        if (context is null)
        {
            return null;
        }

        if (primaryAction == LauncherPrimaryAction.RestartRequired)
        {
            return LauncherPrimaryAction.Continue;
        }

        if (context.HasSetupInterrupted && primaryAction == LauncherPrimaryAction.ContinueSetup)
        {
            return LauncherPrimaryAction.StartOverSetup;
        }

        if (!context.IsDexTorUsb || context.IsSessionRunning || context.HasUncleanSession)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(context.DextorRootPath))
        {
            return null;
        }

        if (primaryAction == LauncherPrimaryAction.StartDexTor || primaryAction == LauncherPrimaryAction.RepairAndStart)
        {
            return LauncherPrimaryAction.OpenDexTor;
        }

        return null;
    }

    private static string PrimaryActionLabel(LauncherPrimaryAction action, ILocalizer localizer)
    {
        return action switch
        {
            LauncherPrimaryAction.Prepare => localizer.T("launcher.primary_action_prepare"),
            LauncherPrimaryAction.Retry => localizer.T("launcher.primary_action_retry"),
            LauncherPrimaryAction.ConnectDexTorUsb => localizer.T("launcher.primary_action_connect_usb"),
            LauncherPrimaryAction.CreateDexTorUsb => localizer.T("launcher.primary_action_create_usb"),
            LauncherPrimaryAction.ContinueSetup => localizer.T("launcher.primary_action_continue_setup"),
            LauncherPrimaryAction.StartOverSetup => localizer.T("launcher.primary_action_start_over_setup"),
            LauncherPrimaryAction.StartDexTor => localizer.T("launcher.primary_action_start_dextor"),
            LauncherPrimaryAction.UpdateAndStart => localizer.T("launcher.primary_action_update_and_start"),
            LauncherPrimaryAction.RestartRequired => localizer.T("launcher.primary_action_restart_required"),
            LauncherPrimaryAction.CheckAndRecover => localizer.T("launcher.primary_action_check_recover"),
            LauncherPrimaryAction.RepairAndStart => localizer.T("launcher.primary_action_repair_and_start"),
            LauncherPrimaryAction.OpenDexTor => localizer.T("launcher.primary_action_open_dextor"),
            LauncherPrimaryAction.EndSession => localizer.T("launcher.primary_action_end_session"),
            LauncherPrimaryAction.EjectUsb => localizer.T("launcher.primary_action_eject_usb"),
            _ => localizer.T("launcher.primary_action_continue"),
        };
    }

    private static IEnumerable<LauncherRequirementRow> BuildRows(PreflightReport report, ILocalizer localizer)
    {
        var compatibilityRequirements = report.Requirements
            .Where(r => r.Kind is PreflightRequirementKind.OperatingSystem or PreflightRequirementKind.ProcessorArchitecture)
            .ToArray();
        var combineCompatibility = compatibilityRequirements.Length == 2;
        var compatibilityAdded = false;

        foreach (var requirement in report.Requirements)
        {
            if (combineCompatibility && requirement.Kind is PreflightRequirementKind.OperatingSystem or PreflightRequirementKind.ProcessorArchitecture)
            {
                if (!compatibilityAdded)
                {
                    yield return BuildCompatibilityRow(compatibilityRequirements, localizer);
                    compatibilityAdded = true;
                }

                continue;
            }

            yield return BuildRow(requirement, localizer);
        }
    }

    private static LauncherRequirementRow BuildCompatibilityRow(
        IReadOnlyList<PreflightRequirement> requirements,
        ILocalizer localizer)
    {
        var state = requirements.MaxBy(r => (int)r.State)!.State;
        var failedRequirements = requirements.Where(r => r.State != PreflightRequirementState.Ok).ToArray();
        var description = failedRequirements.Length == 0
            ? localizer.T("preflight.compatibility.ok")
            : string.Join(" ", failedRequirements.Select(r => localizer.T(r.DescriptionKey, r.DescriptionArgs)));
        var actionableRequirement = failedRequirements.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.ActionUrl));
        var solutionRequirement = failedRequirements.FirstOrDefault(r => r.SolutionTextKey is not null);

        return new LauncherRequirementRow(
            StateLabel(state, localizer),
            state,
            localizer.T("preflight.compatibility.title"),
            description,
            null,
            solutionRequirement?.SolutionTextKey is null
                ? SolutionLabel(state, localizer)
                : localizer.T(solutionRequirement.SolutionTextKey, solutionRequirement.SolutionTextArgs),
            actionableRequirement?.ActionTextKey is null
                ? null
                : localizer.T(actionableRequirement.ActionTextKey),
            actionableRequirement?.ActionUrl);
    }

    private static LauncherRequirementRow BuildRow(PreflightRequirement requirement, ILocalizer localizer)
    {
        return new LauncherRequirementRow(
            StateLabel(requirement.State, localizer),
            requirement.State,
            localizer.T(requirement.TitleKey),
            localizer.T(requirement.DescriptionKey, requirement.DescriptionArgs),
            requirement.Kind == PreflightRequirementKind.LiveImage || requirement.InfoTextKey is null
                ? null
                : localizer.T(requirement.InfoTextKey, requirement.InfoTextArgs),
            requirement.SolutionTextKey is null
                ? SolutionLabel(requirement.State, localizer)
                : localizer.T(requirement.SolutionTextKey, requirement.SolutionTextArgs),
            requirement.Kind != PreflightRequirementKind.LiveImage && !string.IsNullOrWhiteSpace(requirement.ActionTextKey)
                ? localizer.T(requirement.ActionTextKey!)
                : null,
            requirement.Kind == PreflightRequirementKind.LiveImage ? null : requirement.ActionUrl);
    }

    private static string StateLabel(PreflightRequirementState state, ILocalizer localizer)
    {
        return state switch
        {
            PreflightRequirementState.Ok => localizer.T("preflight.state.ok"),
            PreflightRequirementState.Warning => localizer.T("preflight.state.warn"),
            _ => localizer.T("preflight.state.block")
        };
    }

    private static string SolutionLabel(PreflightRequirementState state, ILocalizer localizer)
    {
        return state switch
        {
            PreflightRequirementState.Warning => localizer.T("preflight.solution.warning"),
            PreflightRequirementState.Blocker => localizer.T("preflight.solution.block"),
            _ => string.Empty
        };
    }
}
