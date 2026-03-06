// =============================================================================
// Register Page — Sign Up Form
// =============================================================================
// Registration form with username, email, and password fields.
// Shows validation errors from the API.
// =============================================================================

using Abies.DOM;
using static Abies.Html.Attributes;
using static Abies.Html.Elements;
using static Abies.Html.Events;

namespace Abies.Conduit.Wasm.Pages;

/// <summary>
/// Register page view — sign up form.
/// </summary>
public static class Register
{
    /// <summary>
    /// Renders the registration page.
    /// </summary>
    public static Node View(RegisterModel model) =>
        div([class_("auth-page")],
        [
            div([class_("container page")],
            [
                div([class_("row")],
                [
                    div([class_("col-md-6 offset-md-3 col-xs-12")],
                    [
                        h1([class_("text-xs-center")], [text("Sign up")]),
                        p([class_("text-xs-center")],
                        [
                            a([href("/login")], [text("Have an account?")])
                        ]),
                        Login.ErrorList(model.Errors),
                        Form(model)
                    ])
                ])
            ])
        ]);

    /// <summary>
    /// Renders the registration form.
    /// </summary>
    private static Node Form(RegisterModel model) =>
        form([onsubmit(new RegisterSubmitted())],
        [
            fieldset([class_("form-group")],
            [
                input([
                    class_("form-control form-control-lg"),
                    type("text"),
                    placeholder("Your Name"),
                    value(model.Username),
                    oninput(e => new RegisterUsernameChanged(e?.Value ?? ""))])
            ]),
            fieldset([class_("form-group")],
            [
                input([
                    class_("form-control form-control-lg"),
                    type("email"),
                    placeholder("Email"),
                    value(model.Email),
                    oninput(e => new RegisterEmailChanged(e?.Value ?? ""))])
            ]),
            fieldset([class_("form-group")],
            [
                input([
                    class_("form-control form-control-lg"),
                    type("password"),
                    placeholder("Password"),
                    value(model.Password),
                    oninput(e => new RegisterPasswordChanged(e?.Value ?? ""))])
            ]),
            button([
                class_("btn btn-lg btn-primary pull-xs-right"),
                type("submit"),
                ..model.IsSubmitting ? [disabled()] : Array.Empty<DOM.Attribute>()],
                [text(model.IsSubmitting ? "Signing up..." : "Sign up")])
        ]);
}
