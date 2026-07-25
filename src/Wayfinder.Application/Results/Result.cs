using System;
using System.Collections.Generic;

namespace Wayfinder.Application.Results
{
    // ADO #32 - the shared shape behind CommandResult<T>/QueryResult<T>: a status, an optional error
    // message (set for any non-Success status), and optional non-fatal warnings (e.g. #20's
    // capability-lint findings on a successful deploy). Handlers never throw for expected "bad
    // input" outcomes (see #39's CaseOperations, lifted into the Cases command/query handlers) -
    // they return a typed Result instead, which a future transport layer maps without needing to
    // catch anything.
    public abstract class Result
    {
        protected Result(ResultStatus status, string error, IReadOnlyList<string> warnings)
        {
            Status = status;
            Error = error;
            Warnings = warnings ?? Array.Empty<string>();
        }

        public ResultStatus Status { get; }
        public string Error { get; }
        public IReadOnlyList<string> Warnings { get; }

        public bool Succeeded => Status == ResultStatus.Success;
        public bool Failed => !Succeeded;
    }
}
