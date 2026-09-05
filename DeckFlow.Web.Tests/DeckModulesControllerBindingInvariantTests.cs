using System.Reflection;
using DeckFlow.Web.Controllers;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace DeckFlow.Web.Tests;

/// <summary>
/// Guards the JSON binding contract for Deck Modules POST actions.
/// </summary>
public sealed class DeckModulesControllerBindingInvariantTests
{
    [Fact]
    public void PostActions_RequestParameterIsBoundFromBody()
    {
        var offendingActions = new List<string>();

        foreach (var method in typeof(DeckModulesController).GetMethods())
        {
            if (method.GetCustomAttribute<HttpPostAttribute>() is null)
            {
                continue;
            }

            var requestParameter = method.GetParameters()
                .First(parameter => parameter.ParameterType != typeof(CancellationToken)
                    && !parameter.ParameterType.IsPrimitive);

            if (!requestParameter.IsDefined(typeof(FromBodyAttribute)))
            {
                offendingActions.Add(method.Name);
            }
        }

        Assert.True(
            offendingActions.Count == 0,
            $"POST actions missing [FromBody]: {string.Join(", ", offendingActions)}");
    }
}
