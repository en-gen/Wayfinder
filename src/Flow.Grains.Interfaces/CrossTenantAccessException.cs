using System;
using System.Runtime.Serialization;

namespace Flow.Grains.Interfaces
{
    // ADO #33 - thrown by CaseGrain's public surface (Trigger/GetSnapshot) when the calling
    // CaseRequestContext.TenantId does not match the case's owning tenant (CaseStore.TenantId,
    // stamped at Create from the creating call's CaseRequestContext.TenantId). Deliberately does
    // NOT echo the caseId or either tenant id in the message - the caller must not be able to
    // distinguish "this case belongs to someone else" from "this case does not exist" (a later
    // app-layer sub-unit maps this to the same 404 GetCaseQueryHandler already returns for a
    // never-created case's null-Definition snapshot).
    //
    // [Serializable] + the SerializationInfo constructor are NOT dead legacy ceremony here: unlike
    // every other cross-grain type in this solution (swept with [GenerateSerializer]/[Id(n)]),
    // Exception's own state isn't over settable [Id(n)] properties, so Orleans has no source-gen
    // path for a plain "public class Foo : Exception". Orleans's built-in ExceptionCodec instead
    // piggybacks on the classic ISerializable contract - the same one InvalidOperationException
    // (already thrown across this grain boundary elsewhere in CaseGrain) satisfies out of the box
    // as a BCL type. Omitting this constructor reproduces exactly the failure this comment
    // prevents: Orleans.Serialization.CodecNotFoundException at the moment the grain call response
    // carrying this exception is serialized.
    [Serializable]
    public class CrossTenantAccessException : Exception
    {
        public CrossTenantAccessException()
            : base("The requested case is not accessible in the current tenant context.")
        {
        }

        public CrossTenantAccessException(string message)
            : base(message)
        {
        }

        public CrossTenantAccessException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

#pragma warning disable SYSLIB0051 // formatter-based serialization is obsolete - required for Orleans's ISerializable-based ExceptionCodec, not BinaryFormatter
        protected CrossTenantAccessException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
#pragma warning restore SYSLIB0051
    }
}
