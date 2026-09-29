using System;

namespace Kernels
{
    public enum Lowering
    {
        Fallback,
        Lowered,
        Vectorized,
    }

    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
    public sealed class KernelReportAttribute : Attribute
    {
        public KernelReportAttribute(
            Lowering lowering,
            string detail,
            string sourceFile,
            int sourceLine,
            string reasonFile,
            int reasonLine,
            string[] signature,
            string[] diagnostics,
            string generatedFile,
            string generatedSource)
        {
            Lowering = lowering;
            Detail = detail;
            SourceFile = sourceFile;
            SourceLine = sourceLine;
            ReasonFile = reasonFile;
            ReasonLine = reasonLine;
            Signature = signature;
            Diagnostics = diagnostics;
            GeneratedFile = generatedFile;
            GeneratedSource = generatedSource;
        }

        public Lowering Lowering { get; }

        public string Detail { get; }

        public string SourceFile { get; }

        public int SourceLine { get; }

        public string ReasonFile { get; }

        public int ReasonLine { get; }

        public string[] Signature { get; }

        public string[] Diagnostics { get; }

        public string GeneratedFile { get; }

        public string GeneratedSource { get; }
    }
}
