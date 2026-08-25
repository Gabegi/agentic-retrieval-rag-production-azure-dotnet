namespace AgenticRagApp.Common.Models;

// Base for a structured, source-specific file-open/parse failure category. C# enums
// can't share a base type, and Pdf/Csv each have their own closed set of failure
// categories that make sense for that source - so each source defines its own sealed
// record of static instances deriving from this, instead of one shared enum neither
// source's categories cleanly fit.
//
// Concrete, not abstract, and that is load-bearing: PipelineIssue.Reason is declared as
// this type and rides inside ExtractionStageMetrics across the Durable activity boundary
// (PdfIndexingFunction), where System.Text.Json deserializes by DECLARED type - an
// abstract base throws NotSupportedException there, killing the whole orchestration on
// the first reason-carrying issue (seen live 2026-08-25). Serialization has only ever
// written this base's contract ({"Code":...}), so a round-tripped reason comes back as
// the base with its Code intact; nothing downstream reads the derived type, only Code.
public record OpenFailureReasonBase(string Code);
