using System;
using System.Threading;
using System.Threading.Tasks;
using Flow.Grains.Executables;
using Flow.Grains.Interfaces.Model;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;

namespace Flow.Grains.Expressions
{
    [StatelessWorker]
    public class ExpressionGrain: Grain, IExpressionGrain
    {
        private Guid _caseId;
        private readonly Func<string, IExecutable> _executable;

        private readonly ILogger _logger;

        public ExpressionGrain(
            Func<string, IExecutable> executable,
            ILogger<ExpressionGrain> logger)
        {
            _executable = executable ?? throw new ArgumentNullException(nameof(executable));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            _caseId = this.GetPrimaryKey();
            return base.OnActivateAsync(cancellationToken);
        }

        public async Task<ExecutableResult<bool>> ExecuteAsBool(string contextRef, Expression expression)
        {
            var executor = await BuildExecutable(/*context, */contextRef, expression);
            return executor.ExecuteAsBool();
        }

        public async Task<ExecutableResult<string>> ExecuteAsString(string contextRef, Expression expression)
        {
            var executor = await BuildExecutable(/*context, */contextRef, expression);
            return executor.ExecuteAsString();
        }

        public async Task<ExecutableResult<Iso8601>> ExecuteAsIso8601(/*CaseContext context, */ string contextRef, Expression expression)
        {
            var executor = await BuildExecutable( /*context, */contextRef, expression);
            var result = executor.ExecuteAsString();

            if (result.IsError)
            {
                return ExecutableResult<Iso8601>.Failure(result.Message);
            }

            try
            {
                var iso = new Iso8601(result.Value);

                return ExecutableResult<Iso8601>.Success(iso);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "failed to parse expression result {ExpressionResult} to ISO8601", result.Value);
                return ExecutableResult<Iso8601>.Failure(ex.Message);
            }
        }

        private async Task<IExecutable> BuildExecutable(/*CaseContext context, */string contextRef, Expression expression)
        {
            var executor = _executable(expression.Body);

            // TODO
            //var caseFileGrain = GrainFactory.GetGrain<ICaseFileGrain>(_caseInstanceId, keyExtension: EngineConstants.CaseFileKeyExtension);
            if (contextRef != null)
            {
                //    var caseFileItem = await caseFileGrain.GetCaseFileItem(context, contextRef);
                //    if (caseFileItem != null)
                //    {
                //        executor.WithContext(caseFileItem.Value);
                //    }
            }
            else
            {
                //    var caseFile = await caseFileGrain.GetCaseFile(context);
                //    foreach (var caseFileItem in caseFile)
                //    {
                //        executor.WithArgument(caseFileItem);
                //    }
            }

            // TODO -
            await Task.CompletedTask;

            return executor;
        }
    }
}
