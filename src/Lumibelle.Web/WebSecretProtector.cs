using lumibelle.Services.AI;
using Microsoft.AspNetCore.DataProtection;
namespace lumibelle;
public sealed class WebSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector protector = provider.CreateProtector("Lumibelle.AiCredentials.v1");
    public string Protect(string value) => protector.Protect(value);
    public string Unprotect(string value) => protector.Unprotect(value);
}
