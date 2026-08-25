namespace TradeAgent.Core;

/// <summary>Durable onboarding steps, in order. Progress lives in the database, not in the UI.</summary>
public enum OnboardingStep
{
    WELCOME, SYSTEM_CHECK,
    AI_RUNTIME_SELECTED, AI_RUNTIME_INSTALLED, AI_AUTHENTICATED,
    TRADING_PLATFORM_SELECTED, ATAS_INSTALLED, ATAS_BRIDGE_INSTALLED, ATAS_BRIDGE_CONNECTED,
    TRADING_CONNECTION_FOUND, ACCOUNT_SELECTED, MARKET_DATA_VERIFIED, ORDER_ACCESS_VERIFIED,
    WORKSPACE_CREATED, AGENT_READY,
    SETUP_COMPLETE
}

public static class OnboardingSteps
{
    public static readonly OnboardingStep[] Order = Enum.GetValues<OnboardingStep>();

    /// <summary>Plain-language title shown to a nontechnical user.</summary>
    public static string Title(this OnboardingStep s) => s switch
    {
        OnboardingStep.WELCOME                  => "Welcome",
        OnboardingStep.SYSTEM_CHECK             => "Checking your computer",
        OnboardingStep.AI_RUNTIME_SELECTED      => "Choose your AI assistant",
        OnboardingStep.AI_RUNTIME_INSTALLED     => "Installing the AI assistant",
        OnboardingStep.AI_AUTHENTICATED         => "Sign in to your AI account",
        OnboardingStep.TRADING_PLATFORM_SELECTED=> "Choose your trading platform",
        OnboardingStep.ATAS_INSTALLED           => "Finding ATAS",
        OnboardingStep.ATAS_BRIDGE_INSTALLED    => "Installing the ATAS bridge",
        OnboardingStep.ATAS_BRIDGE_CONNECTED    => "Connecting to ATAS",
        OnboardingStep.TRADING_CONNECTION_FOUND => "Finding your trading connection",
        OnboardingStep.ACCOUNT_SELECTED         => "Choose your account",
        OnboardingStep.MARKET_DATA_VERIFIED     => "Checking live prices",
        OnboardingStep.ORDER_ACCESS_VERIFIED    => "Checking trading access",
        OnboardingStep.WORKSPACE_CREATED        => "Creating the AI's workspace",
        OnboardingStep.AGENT_READY              => "Starting the AI",
        OnboardingStep.SETUP_COMPLETE           => "Setup complete",
        _ => s.ToString()
    };
}
