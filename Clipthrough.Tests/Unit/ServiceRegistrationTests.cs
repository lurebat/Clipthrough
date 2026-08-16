using System;
using System.Linq;
using Clipthrough;
using Clipthrough.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// The container is the one part of the app that a green build says nothing about: a service
/// whose constructor gains a parameter nobody registered still compiles, and only fails when
/// the provider first resolves it. For a desktop app that is a crash on launch, after the
/// installer has already run.
///
/// These tests validate the registrations without constructing anything. ValidateOnBuild
/// builds a call site per descriptor rather than invoking constructors, so nothing here opens
/// the database, reads settings, or touches the user's profile.
/// </summary>
public class ServiceRegistrationTests
{
    [Fact]
    public void EveryRegisteredService_CanBeResolved()
    {
        var services = App.CreateServiceCollection();

        // ValidateOnBuild reports every unsatisfiable registration at once rather than
        // failing on the first, so a break names all of them.
        var exception = Record.Exception(() => services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }).Dispose());

        Assert.Null(exception);
    }

    /// <summary>
    /// MainWindowViewModel is the widest constructor in the app and the one the main window
    /// resolves during startup, so it is the registration most likely to rot and the most
    /// expensive to get wrong.
    /// </summary>
    [Fact]
    public void MainWindowViewModel_HasEveryConstructorDependencyRegistered()
    {
        var services = App.CreateServiceCollection();
        var registered = services.Select(descriptor => descriptor.ServiceType).ToHashSet();

        var constructor = Assert.Single(typeof(MainWindowViewModel).GetConstructors());

        var missing = constructor.GetParameters()
            .Select(parameter => parameter.ParameterType)
            .Where(type => !registered.Contains(type))
            .Select(type => type.FullName)
            .ToArray();

        Assert.Empty(missing);
    }
}
