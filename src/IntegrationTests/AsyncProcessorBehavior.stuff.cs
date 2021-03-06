﻿using System;
using System.Linq.Expressions;
using System.Net.Http;
using System.Threading.Tasks;
using IntegrationTest.Share;
using Microsoft.Extensions.DependencyInjection;
using MyLab.ApiClient;
using MyLab.ApiClient.Test;
using MyLab.AsyncProcessor.Sdk;
using MyLab.AsyncProcessor.Sdk.DataModel;
using MyLab.AsyncProcessor.Sdk.Processor;
using MyLab.Mq;
using MyLab.Mq.Communication;
using MyLab.Mq.MqObjects;
using MyLab.Redis;
using MyLab.Syslog;
using Newtonsoft.Json;
using TestProcessor;
using Xunit;
using Xunit.Abstractions;
using AsyncProcessorOptions = MyLab.AsyncProcessor.Api.AsyncProcessorOptions;
using Startup = MyLab.AsyncProcessor.Api.Startup;

namespace IntegrationTests
{
    public partial class AsyncProcessorBehavior
    {
        private readonly TestApi<Startup, IAsyncProcessorRequestsApi> _asyncProcTestApi;
        private readonly TestApi<TestProcessor.Startup, IProcessorApi> _procApi;
        private readonly ITestOutputHelper _output;

        

        /// <summary>
        /// Initializes a new instance of <see cref="AsyncProcessorBehavior"/>
        /// </summary>
        public AsyncProcessorBehavior(
            TestApi<Startup, IAsyncProcessorRequestsApi> asyncProcTestApi,
            TestApi<TestProcessor.Startup, IProcessorApi> procApi,
            ITestOutputHelper output
        )
        {
            _asyncProcTestApi = asyncProcTestApi;
            _procApi = procApi;
            _output = output;
        }

        (MqQueue incomingMq, MqExchange exchange) CreateCallback()
        {
            string exchName = "async-proc-test:callback:" + Guid.NewGuid().ToString("N");
            var exchange = TestTools.ExchangeFactory(autoDelete:false).CreateWithName(exchName);
            
            string queueName = "async-proc-test:callback:" + Guid.NewGuid().ToString("N");
            var queue = TestTools.QueueFactory(autoDelete:false).CreateWithName(queueName);

            queue.BindToExchange(exchange, "foo-callback");

            return (queue, exchange);
        }

        private (TestApiClient<IAsyncProcessorRequestsApi> AsyncProcApi, TestApiClient<IProcessorApi> ProcApi) Prepare(string callbackExchangeName)
        {
            var deadLetterExchange = CreateDeadLetterExchange();
            var deadLetterQueue = CreateQueue(null, "async-proc-test:dead-letter:");
            deadLetterQueue.BindToExchange(deadLetterExchange);

            var queue = CreateQueue(deadLetterExchange);

            var asyncProcApi = StartAsyncProcApi(queue, deadLetterQueue, callbackExchangeName, out var asyncProcApiInnerClient);
            var processorApi = StartProcessor(queue, asyncProcApiInnerClient);

            return (asyncProcApi, processorApi);
        }

        private async Task<string> SendRequest(
            TestRequest request,
            (TestApiClient<IAsyncProcessorRequestsApi> AsyncProcApi, TestApiClient<IProcessorApi> ProcApi) api,
            string predefinedId = null)
        {
            var createRequest = new CreateRequest
            {
                RequestId = predefinedId,
                Content = SerializeRequest(request),
                CallbackRouting = "foo-callback"
            };

            var reqIdResp = await api.AsyncProcApi.Call(s => s.CreateAsync(createRequest));
            return reqIdResp.ResponseContent;
        }

        private async Task<RequestStatus> ProcessRequest(
            string reqId,
            (TestApiClient<IAsyncProcessorRequestsApi> AsyncProcApi, TestApiClient<IProcessorApi> ProcApi) api)
        {
            await api.ProcApi.Call(p => p.GetStatus());

            TestCallDetails<RequestStatus> statusResp = await api.AsyncProcApi.Call(s => s.GetStatusAsync(reqId));
            int tryCount = 0;

            while (statusResp.ResponseContent.Step != ProcessStep.Completed && tryCount++ < 10)
            {
                await Task.Delay(200);
                statusResp = await api.AsyncProcApi.Call(s => s.GetStatusAsync(reqId));
            }

            if (statusResp.ResponseContent.Step != ProcessStep.Completed)
                throw new TimeoutException("Waiting for response timeout");

            return statusResp.ResponseContent;
        }

        private async Task<T> GetResult<T>(
            (TestApiClient<IAsyncProcessorRequestsApi> AsyncProcApi, TestApiClient<IProcessorApi> ProcApi) api,
            Expression<Func<IAsyncProcessorRequestsApi, Task<T>>> call)
        {
            var resResp = await api.AsyncProcApi.Call(call);

            return resResp.ResponseContent;
        }

        private string SerializeRequest(TestRequest request)
        {
            return JsonConvert.SerializeObject(request);
        }

        private MqQueue CreateQueue(MqExchange deadLetterExchange, string prefix = null)
        {
            var queueFactory = TestTools.QueueFactory();
            queueFactory.DeadLetterExchange = deadLetterExchange?.Name;

            string name = (prefix ?? "async-proc-test:queue:") + Guid.NewGuid().ToString("N");
            return queueFactory.CreateWithName(name);
        }

        private MqExchange CreateDeadLetterExchange()
        {
            var exchangeFactory = TestTools.ExchangeFactory(MqExchangeType.Fanout);

            string name = "async-proc-test:dead-letter:" + Guid.NewGuid().ToString("N") + ":dead-letter";
            return exchangeFactory.CreateWithName(name);
        }

        private TestApiClient<IProcessorApi> StartProcessor(MqQueue queue, HttpClient asyncProcApiClient)
        {
            var tc = _procApi.Start(srv =>
            {
                srv.Configure<MqOptions>(opt =>
                {
                    opt.Host = TestTools.MqOptions.Host;
                    opt.Port = TestTools.MqOptions.Port;
                    opt.User = TestTools.MqOptions.User;
                    opt.Password = TestTools.MqOptions.Password;
                });

                srv.Configure<MyLab.AsyncProcessor.Sdk.Processor.AsyncProcessorOptions>(opt =>
                {
                    opt.Queue = queue.Name;
                });

                srv.AddApiClients(reg => { reg.RegisterContract<IAsyncProcessorRequestsApi>(); },
                    new SingleHttpClientFactory(asyncProcApiClient));

                srv.AddSingleton<IWebCallReporterFactory>(new WebCallReporterFactory(_output));
            });


            tc.Output = _output;

            return tc;
        }

        private TestApiClient<IAsyncProcessorRequestsApi> StartAsyncProcApi(
            MqQueue queue,
            MqQueue deadLetterQueue,
            string callbackExchangeName,
            out HttpClient innerHttpClient)
        {
            var tc = _asyncProcTestApi.Start(out innerHttpClient, srv =>
            {
                srv.Configure<SyslogLoggerOptions>(opt =>
                {
                    opt.RemoteHost = "localhost";
                    opt.RemotePort = 123456;
                });

                srv.Configure<MqOptions>(opt =>
                {
                    opt.Host = TestTools.MqOptions.Host;
                    opt.Port = TestTools.MqOptions.Port;
                    opt.User = TestTools.MqOptions.User;
                    opt.Password = TestTools.MqOptions.Password;
                });

                srv.Configure<RedisOptions>(opt => { opt.ConnectionString = "localhost:10201,allowAdmin=true"; });

                srv.Configure<AsyncProcessorOptions>(opt =>
                {
                    opt.MaxIdleTime = TimeSpan.FromMinutes(5);
                    opt.QueueRoutingKey = queue.Name;
                    opt.MaxStoreTime = TimeSpan.FromMinutes(5);
                    opt.RedisKeyPrefix = "async-proc-test:" + Guid.NewGuid().ToString("N");
                    opt.DeadLetter = deadLetterQueue.Name;
                    opt.Callback = callbackExchangeName;
                });
            });

            tc.Output = _output;

            return tc;
        }

        private class SingleHttpClientFactory : IHttpClientFactory
        {
            private readonly HttpClient _client;

            public SingleHttpClientFactory(HttpClient client)
            {
                _client = client;
            }

            public HttpClient CreateClient(string name)
            {
                return _client;
            }
        }

        async Task AssertCallback(MqQueue callbackQueue, Action<ChangeStatusCallbackMessage>[] asserts)
        {
            await Task.Delay(500);

            for (int i = 0; i < asserts.Length; i++)
            {
                var assert = asserts[i];
                MqMessage<ChangeStatusCallbackMessage> msg;

                try
                {
                    var rm = callbackQueue.Listen<ChangeStatusCallbackMessage>(TimeSpan.FromSeconds(5));
                    rm.Ack();

                    Assert.NotNull(rm.Message);

                    assert(rm.Message.Payload);
                }
                catch (TimeoutException)
                {
                    Assert.True(false, $"Callback msg #'{i}' read timeout");
                }
            }
        }
    }
}