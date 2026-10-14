using TUnit.Core.Interfaces;

namespace RagAssistant.Integration.Tests.Fixtures;

/// <summary>Each ingestion test builds a whole application with its own connection pools; cap how many run at once.</summary>
public sealed class IngestionParallelLimit : IParallelLimit
{
    public int Limit => 4;
}
