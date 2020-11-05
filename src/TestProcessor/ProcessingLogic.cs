using System;
using System.Text;
using System.Threading.Tasks;
using IntegrationTest.Share;
using MyLab.AsyncProcessor.Sdk.Processor;

namespace TestProcessor
{
    class ProcessingLogic : IAsyncProcessingLogic<TestRequest>
    {
        public Task ProcessAsync(TestRequest request, IProcessingOperator op)
        {
            switch (request.Command)
            {
                case "concat":
                    return op.CompleteWithResultAsync(request.Value1 + "-" + request.Value2);
                case "incr-int":
                    return op.CompleteWithResultAsync(request.Value2+1);
                case "str-to-bin":
                    return op.CompleteWithResultAsync(Encoding.UTF8.GetBytes(request.Value1));
                default: throw new IndexOutOfRangeException();
            }
        }
    }
}