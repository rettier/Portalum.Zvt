using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Portalum.Zvt.Helpers;
using Portalum.Zvt.Models;

namespace Portalum.Zvt
{
    /// <summary>
    /// ZvtCommunication, automatic completion processing
    /// This middle layer filters out completion packages and forwards the other data
    /// </summary>
    public class ZvtCommunication : IDisposable
    {
        private readonly ILogger _logger;
        private readonly IDeviceCommunication _deviceCommunication;

        private CancellationTokenSource _acknowledgeReceivedCancellationTokenSource;
        private CancellationTokenSource _sendCommandAsyncCancellationTokenSource;
        private byte[] _dataBuffer;
        private bool _waitForAcknowledge = false;

        private readonly object _completionLock = new object();
        private bool _completionPending = false;
        private bool _asyncCompletion = false;

        /// <summary>
        /// New data received from the pt device
        /// </summary>
        public event Func<byte[], ProcessData> DataReceived;

        /// <summary>
        /// A callback which is checked 
        /// </summary>
        public event Func<CompletionInfo> GetCompletionInfo;

        private readonly byte[] _positiveCompletionData1 = new byte[] { 0x80, 0x00, 0x00 }; //Default
        private readonly byte[] _positiveCompletionData2 = new byte[] { 0x84, 0x00, 0x00 }; //Alternative
        private readonly byte[] _positiveCompletionData3 = new byte[] { 0x84, 0x9C, 0x00 }; //Special case for request more time
        private readonly byte[] _negativeIssueGoodsData = new byte[] { 0x84, 0x66, 0x00 };
        private readonly byte[] _otherCommandData = new byte[] { 0x84, 0x83, 0x00 };
        private readonly byte _negativeCompletionPrefix = 0x84;

        /// <summary>
        /// ZvtCommunication
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="deviceCommunication"></param>
        public ZvtCommunication(
            ILogger logger,
            IDeviceCommunication deviceCommunication)
        {
            this._logger = logger;
            this._deviceCommunication = deviceCommunication;
            this._deviceCommunication.DataReceived += this.DataReceiveSwitch;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Dispose
        /// </summary>
        /// <param name="disposing"></param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                this._deviceCommunication.DataReceived -= this.DataReceiveSwitch;
            }
        }

        /// <summary>
        /// Switch for incoming data
        /// </summary>
        /// <param name="data"></param>
        private void DataReceiveSwitch(byte[] data)
        {
            if (this._waitForAcknowledge)
            {
                this.AddDataToBuffer(data);
                return;
            }

            this.ProcessData(data);
        }

        private void AddDataToBuffer(byte[] data)
        {
            this._dataBuffer = data;
            this._acknowledgeReceivedCancellationTokenSource?.Cancel();
        }

        /// <summary>
        /// Sends completion information to the PT in case the PT is currently waiting for completion of the ECR.
        /// This can be called to more quickly complete a pending transaction, instead of waiting for the PT
        /// to poll the information.
        /// </summary>
        /// <param name="completionInfo">The completion info to be sent.</param>
        public void SendCompletionInfo(CompletionInfo completionInfo)
        {
            _logger.LogDebug("SendCompletionInfo, completionInfo: " + completionInfo);
            lock (_completionLock)
            {
                if (!_completionPending)
                {
                    this._logger.LogWarning("No completion was pending. Completion info will be ignored.");
                    return;
                }
                
                _completionPending = false;
                switch (completionInfo.State)
                {
                    case CompletionInfoState.ChangeAmount:
                        var controlField = new byte[] { 0x84, 0x9D };

                        // Change the amount from the original in the start request
                        var package = new List<byte>();
                        package.Add(0x04); //Amount prefix
                        package.AddRange(NumberHelper.DecimalToBcd(completionInfo.Amount));
                        this._deviceCommunication.SendAsync(PackageHelper.Create(controlField, package.ToArray()));
                        break;
                    case CompletionInfoState.Successful:
                        this._deviceCommunication.SendAsync(this._positiveCompletionData1);
                        break;
                    case CompletionInfoState.Failure:
                        this._deviceCommunication.SendAsync(this._negativeIssueGoodsData);
                        break;
                    default:
                        throw new NotImplementedException();
                    case CompletionInfoState.Wait:
                        throw new InvalidOperationException(
                            "Cannot explicitly send a wait status. This is handled automatically.");
                }
            }
        }

        private void ProcessData(byte[] data)
        {
            var dataProcessed = this.DataReceived?.Invoke(data);
            if (dataProcessed?.State == ProcessDataState.Processed)
            {
                var completionInfo = this.GetCompletionInfo?.Invoke();
                if (completionInfo == null)
                {
                    _asyncCompletion = false;
                    //Default if no one has subscribed to the event, immediately approve the transaction
                    this._deviceCommunication.SendAsync(this._positiveCompletionData1);
                } 
                else
                {
                    if (completionInfo.State == CompletionInfoState.Wait)
                    {
                        this._completionPending = true;
                        Task.Delay(10000, this._sendCommandAsyncCancellationTokenSource.Token).ContinueWith(task =>
                        {
                            if (this._completionPending && task.Status == TaskStatus.RanToCompletion)
                            {
                                this._deviceCommunication.SendAsync(this._positiveCompletionData3);
                            }
                        });
                    }
                    else
                    {
                        this.SendCompletionInfo(completionInfo);
                    }
                }
            }
        }

        /// <summary>
        /// Send command
        /// </summary>
        /// <param name="commandData">The data of the command</param>
        /// <param name="acknowledgeReceiveTimeoutMilliseconds">Maximum waiting time for the acknowledge package, default is 5 seconds, T3 Timeout</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<SendCommandResult> SendCommandAsync(
            byte[] commandData,
            int acknowledgeReceiveTimeoutMilliseconds = 5000,
            CancellationToken cancellationToken = default)
        {
            this.ResetDataBuffer();

            this._acknowledgeReceivedCancellationTokenSource?.Dispose();
            this._acknowledgeReceivedCancellationTokenSource = new CancellationTokenSource();

            this._sendCommandAsyncCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this._acknowledgeReceivedCancellationTokenSource.Token);
            try
            {

                this._waitForAcknowledge = true;
                try
                {
                    await this._deviceCommunication.SendAsync(commandData, _sendCommandAsyncCancellationTokenSource.Token)
                        .ContinueWith(task => { });
                }
                catch (Exception exception)
                {
                    this._logger.LogError(exception, $"{nameof(SendCommandAsync)} - Cannot send data");
                    this._acknowledgeReceivedCancellationTokenSource.Dispose();
                    return SendCommandResult.SendFailure;
                }

                await Task.Delay(acknowledgeReceiveTimeoutMilliseconds, _sendCommandAsyncCancellationTokenSource.Token)
                    .ContinueWith(task =>
                    {
                        if (task.Status == TaskStatus.RanToCompletion)
                        {
                            this._logger.LogError(
                                $"{nameof(SendCommandAsync)} - Wait task for acknowledge was aborted");
                        }

                        this._waitForAcknowledge = false;
                    });

                this._acknowledgeReceivedCancellationTokenSource.Dispose();

                if (this._dataBuffer == null)
                {
                    return SendCommandResult.NoDataReceived;
                }

                if (this.CheckIsPositiveCompletion())
                {
                    this.ForwardUnusedBufferData();

                    return SendCommandResult.PositiveCompletionReceived;
                }

                if (this.CheckIsNotSupported())
                {
                    return SendCommandResult.NotSupported;
                }

                if (this.CheckIsNegativeCompletion())
                {
                    this._logger.LogError($"{nameof(SendCommandAsync)} - 'Negative completion' received");
                    return SendCommandResult.NegativeCompletionReceived;
                }

                return SendCommandResult.UnknownFailure;
            }
            finally
            {
                this._sendCommandAsyncCancellationTokenSource?.Cancel();
            }
            
        }

        private bool CheckIsPositiveCompletion()
        {
            if (this._dataBuffer.Length < 3)
            {
                return false;
            }

            var buffer = this._dataBuffer.AsSpan().Slice(0, 3);

            if (buffer.SequenceEqual(this._positiveCompletionData1))
            {
                return true;
            }

            if (buffer.SequenceEqual(this._positiveCompletionData2))
            {
                return true;
            }

            if (buffer.SequenceEqual(this._positiveCompletionData3))
            {
                return true;
            }

            return false;
        }

        private bool CheckIsNegativeCompletion()
        {
            if (this._dataBuffer.Length < 3)
            {
                return false;
            }

            if (this._dataBuffer[0] == this._negativeCompletionPrefix)
            {
                var errorByte = this._dataBuffer[1];
                this._logger.LogDebug($"{nameof(CheckIsNegativeCompletion)} - ErrorCode:{errorByte:X2}");

                return true;
            }

            return false;
        }

        private bool CheckIsNotSupported()
        {
            if (this._dataBuffer.Length < 3)
            {
                return false;
            }

            var buffer = this._dataBuffer.AsSpan().Slice(0, 3);

            if (buffer.SequenceEqual(this._otherCommandData))
            {
                return true;
            }

            return false;
        }

        private void ResetDataBuffer()
        {
            this._dataBuffer = null;
        }

        private void ForwardUnusedBufferData()
        {
            if (this._dataBuffer.Length == 3)
            {
                this.ResetDataBuffer();
                return;
            }

            var unusedData = this._dataBuffer.AsSpan().Slice(3).ToArray();
            this.ProcessData(unusedData);
        }
    }
}