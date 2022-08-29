using Portalum.Zvt.Responses;

namespace Portalum.Zvt.Models;

/// <summary>
/// a class describing the result of the IReceiveHandler's data processing
/// </summary>
public class ProcessData
{
    /// <summary>
    /// Current State of the data processing
    /// </summary>
    public ProcessDataState State { get; set; }

    /// <summary>
    /// Current State of the data processing
    /// </summary>
    public IResponse Response { get; set; } = null;

    public ProcessData(ProcessDataState state)
    {
        this.State = state;
    }

    public ProcessData(ProcessDataState state, IResponse response)
    {
        this.State = state;
        this.Response = response;
    }
}