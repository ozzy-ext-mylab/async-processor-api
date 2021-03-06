﻿using System;
using System.Text;
using MyLab.AsyncProcessor.Sdk.DataModel;
using MyLab.LogDsl;
using MyLab.Mq;
using MyLab.Mq.PubSub;

namespace MyLab.AsyncProcessor.Api.Tools
{
    class CallbackReporter
    {
        private readonly IMqPublisher _mqPublisher;
        private readonly string _callbackQueue;

        public DslLogger Log { get; set; }

        public CallbackReporter(IMqPublisher mqPublisher, string callbackQueue)
        {
            _mqPublisher = mqPublisher;
            _callbackQueue = callbackQueue;
        }

        //public void SendStartProcessing(string requestId, string callbackRouting)
        //{
        //    SendCallbackMessage(
        //        callbackRouting,
        //        new ChangeStatusCallbackMessage
        //    {
        //        RequestId = requestId,
        //        NewProcessStep = ProcessStep.Processing
        //    });
        //}

        public void SendCompletedWithResult(string requestId, string callbackRouting, byte[] resultBin, string mimeType)
        {
            var msg = new ChangeStatusCallbackMessage
            {
                RequestId = requestId,
                NewProcessStep = ProcessStep.Completed
            };

            switch (mimeType)
            {
                case "application/octet-stream":
                {
                    msg.ResultBin = resultBin;
                }
                    break;
                case "application/json":
                {
                    msg.ResultObjectJson = Encoding.UTF8.GetString(resultBin);
                }
                    break;
                default:
                {
                    throw new UnsupportedMediaTypeException(mimeType);
                }
            }


            SendCallbackMessage(
                callbackRouting,
                msg);
        }

        public void SendCompletedWithError(string requestId, string callbackRouting, ProcessingError procError)
        {
            SendCallbackMessage(
                callbackRouting,
                new ChangeStatusCallbackMessage
            {
                RequestId = requestId,
                NewProcessStep = ProcessStep.Completed,
                OccurredError = procError
            });
        }

        public void SendRequestStep(string requestId, string callbackRouting, ProcessStep step)
        {
            SendCallbackMessage(
                callbackRouting,
                new ChangeStatusCallbackMessage
            {
                RequestId = requestId,
                NewProcessStep = step
            });
        }

        public void SendBizStepChanged(string requestId, string callbackRouting, string newBizStep)
        {
            SendCallbackMessage(
                callbackRouting,
                new ChangeStatusCallbackMessage
            {
                RequestId = requestId,
                NewBizStep = newBizStep
            });
        }

        void SendCallbackMessage(string callbackRouting, ChangeStatusCallbackMessage msg)
        {
            try
            {
                _mqPublisher.Publish(new OutgoingMqEnvelop<ChangeStatusCallbackMessage>
                {
                    PublishTarget = new PublishTarget { Exchange = _callbackQueue, Routing = callbackRouting },
                    Message = new MqMessage<ChangeStatusCallbackMessage>(msg)
                });
            }
            catch (Exception e)
            {
                Log.Error("Can't send callback message", e).Write();
            }
        }
    }
}
