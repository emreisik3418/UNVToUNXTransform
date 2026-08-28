namespace UNVToUNXTransform.Models;

public class AuthenticationRequest
{
    public string UserName { get; set; }
    public string Password { get; set; }
    public string AuthType { get; set; } = "secEnterprise";
}
