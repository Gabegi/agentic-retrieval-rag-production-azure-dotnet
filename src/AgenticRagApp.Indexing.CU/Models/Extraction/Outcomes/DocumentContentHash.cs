namespace AgenticRagApp.Indexing.CU.Models;

// One document's SHA-256 over its raw downloaded bytes, paired with the blob it came from.
//
// Carried out of extraction for reporting only - see ExtractionOutputBuilder's content-hashing
// section for why this is deliberately not a cache key. Ok distinguishes a document that hashed
// and extracted from one that hashed and then failed to analyze; both are hashed, because the
// hash is taken before anything is submitted. A file whose download produced no bytes has no
// entry here at all.
public sealed record DocumentContentHash(string BlobName, string Hash, bool Ok);
