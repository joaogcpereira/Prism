// ============================================================
//  Config.cs  (Prism.Agent.Contracts)
//  Local uploader configuration, read from
//  %ProgramData%\Prism\Agent\config.json (written by install.ps1
//  / an Intune config profile). Absent or no GatewayUrl => the
//  uploader stays disabled and batches simply spool locally.
// ============================================================
namespace Prism.Agent.Contracts;

public sealed record UploaderConfig(
    string? GatewayUrl,                  // e.g. https://gateway.contoso.com/api/v1/usage
    string? CertThumbprint = null,       // preferred device-cert selector (LocalMachine\My)
    string? CertIssuer = null,           // fallback selector: issuer substring match
    string? ServerCertThumbprint = null, // optional: pin the gateway's server cert
    int UploadIntervalSeconds = 60,
    int MaxBatchesPerCycle = 50,
    bool CompressUploads = true);        // gzip batches >1 KB (gateway decompresses)
