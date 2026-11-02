using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();

var app = builder.Build();

var users = new Dictionary<int, User>();
var nextId = 1;
var userLock = new object();

var apiToken = Environment.GetEnvironmentVariable("API_TOKEN");

if (string.IsNullOrWhiteSpace(apiToken))
{
    throw new InvalidOperationException("Set API_TOKEN before starting the API.");
}


// catches errors so the whole thing doesnt fall over
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (Exception error)
    {
        app.Logger.LogError(error, "Request failed");

        if (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = 500;

            await context.Response.WriteAsJsonAsync(new
            {
                error = "Internal server error."
            });
        }
    }
});


// basic token check before anyone gets near the endpoints
app.Use(async (context, next) =>
{
    var suppliedToken = context.Request.Headers.Authorization.ToString();

    if (suppliedToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        suppliedToken = suppliedToken["Bearer ".Length..].Trim();
    }
    else
    {
        suppliedToken = "";
    }

    var expectedBytes = Encoding.UTF8.GetBytes(apiToken);
    var suppliedBytes = Encoding.UTF8.GetBytes(suppliedToken);

    var validToken = expectedBytes.Length == suppliedBytes.Length &&
                     CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);

    if (!validToken)
    {
        // still log denied requests so they dont disappear
        app.Logger.LogWarning("Unauthorized {Method} {Path} returned 401",
            context.Request.Method,
            context.Request.Path);

        context.Response.StatusCode = 401;

        await context.Response.WriteAsJsonAsync(new
        {
            error = "Unauthorized. A valid bearer token is required."
        });

        return;
    }

    await next(context);
});


// logs the request and what the api sends back
app.Use(async (context, next) =>
{
    await next(context);

    app.Logger.LogInformation("{Method} {Path} returned {StatusCode}",
        context.Request.Method,
        context.Request.Path,
        context.Response.StatusCode);
});


app.MapGet("/users", () =>
{
    lock (userLock)
    {
        return Results.Ok(users.Values.OrderBy(user => user.Id).ToList());
    }
});


app.MapGet("/users/{id:int}", (int id) =>
{
    lock (userLock)
    {
        if (!users.TryGetValue(id, out var user))
        {
            return Results.NotFound(new
            {
                error = "User not found."
            });
        }

        return Results.Ok(user);
    }
});


app.MapPost("/users", (UserInput input) =>
{
    var validationError = ValidateUser(input);

    if (validationError != null)
    {
        return Results.BadRequest(new
        {
            error = validationError
        });
    }

    lock (userLock)
    {
        // just making sure we dont accidentally reuse an email
        if (users.Values.Any(user => user.Email.Equals(input.Email.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return Results.Conflict(new
            {
                error = "Email already exists."
            });
        }

        var user = new User(nextId++, input.Name.Trim(), input.Email.Trim());

        users.Add(user.Id, user);

        return Results.Created($"/users/{user.Id}", user);
    }
});


app.MapPut("/users/{id:int}", (int id, UserInput input) =>
{
    var validationError = ValidateUser(input);

    if (validationError != null)
    {
        return Results.BadRequest(new
        {
            error = validationError
        });
    }

    lock (userLock)
    {
        if (!users.ContainsKey(id))
        {
            return Results.NotFound(new
            {
                error = "User not found."
            });
        }

        if (users.Values.Any(user => user.Id != id &&
                                     user.Email.Equals(input.Email.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return Results.Conflict(new
            {
                error = "Email already exists."
            });
        }

        var updatedUser = new User(id, input.Name.Trim(), input.Email.Trim());

        users[id] = updatedUser;

        return Results.Ok(updatedUser);
    }
});


app.MapDelete("/users/{id:int}", (int id) =>
{
    lock (userLock)
    {
        if (!users.Remove(id))
        {
            return Results.NotFound(new
            {
                error = "User not found."
            });
        }

        return Results.NoContent();
    }
});


app.Run();


// no empty names or dodgy email addresses
static string? ValidateUser(UserInput input)
{
    if (string.IsNullOrWhiteSpace(input.Name))
    {
        return "Name is required.";
    }

    if (input.Name.Trim().Length > 100)
    {
        return "Name cannot be longer than 100 characters.";
    }

    if (string.IsNullOrWhiteSpace(input.Email) || input.Email.Length > 254)
    {
        return "A valid email is required.";
    }

    if (!MailAddress.TryCreate(input.Email.Trim(), out var email) ||
        email.Address != input.Email.Trim() ||
        !email.Address.Contains('.'))
    {
        return "A valid email is required.";
    }

    return null;
}


record User(int Id, string Name, string Email);

record UserInput(string Name, string Email);
