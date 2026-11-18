using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    [HttpPost("register")]
    public IActionResult Register([FromBody] AuthDto model)
    {
        if (string.IsNullOrEmpty(model.Login) || string.IsNullOrEmpty(model.Password))
        {
            return BadRequest(new { message = "Логин и пароль обязательны" });
        }
        return Ok(new { message = "Регистрация успешна" });
    }

    [HttpPost("login")]
    public IActionResult Login([FromBody] AuthDto model)
    {
        if (string.IsNullOrEmpty(model.Login))
        {
            return BadRequest(new { message = "Неверный логин" });
        }

        var fakeToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkNsaWVudCIsImlhdCI6MTUxNjIzOTAyMn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";

        return Ok(new { token = fakeToken, user = model.Login });
    }
}