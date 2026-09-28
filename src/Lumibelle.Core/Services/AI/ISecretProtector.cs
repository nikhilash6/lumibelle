namespace lumibelle.Services.AI;
public interface ISecretProtector
{
    string Protect(string value);
    string Unprotect(string value);
}
