using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace RagAssistant.Modules.Chat.Options;

/// <summary>The follow-up rewrite (spec 014). History is used by the rewrite only, never by the answer prompt.</summary>
internal sealed class FollowUpOptions
{
    public const string Section = "Chat:FollowUp";

    /// <summary>How many earlier exchanges (question plus answer) the rewrite sees.</summary>
    [Range(1, 10)]
    public int HistoryExchanges { get; set; } = 3;

    /// <summary>A longer earlier answer is cut to this many characters.</summary>
    [Range(100, int.MaxValue)]
    public int MaxAnswerChars { get; set; } = 1000;

    public TimeSpan RewriteTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>The rewrite must be able to time out before the whole answer does.</summary>
internal sealed class FollowUpOptionsValidator(IOptions<GenerationOptions> generation) : IValidateOptions<FollowUpOptions>
{
    public ValidateOptionsResult Validate(string? name, FollowUpOptions options) =>
        options.RewriteTimeout > TimeSpan.Zero && options.RewriteTimeout < generation.Value.TotalTimeout
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Chat:FollowUp:RewriteTimeout must be greater than zero and less than Chat:Generation:TotalTimeout.");
}
