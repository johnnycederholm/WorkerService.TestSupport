// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyModel;
using Microsoft.Extensions.Hosting;
using WorkerService.Testing;

namespace WorkerService.Testing;

/// <summary>
/// Factory for bootstrapping an application in memory for functional end to end tests.
/// </summary>
/// <typeparam name="TEntryPoint">A type in the entry point assembly of the application.
/// Typically the Startup or Program classes can be used.</typeparam>
public class WorkerServiceFactory<TEntryPoint> : IDisposable, IAsyncDisposable where TEntryPoint : class
{
    private bool _disposed;
    private bool _disposedAsync;
    private IHost? _host;
    private Action<IHostBuilder> _configuration;
    private readonly List<WorkerServiceFactory<TEntryPoint>> _derivedFactories = new();

    /// <summary>
    /// <para>
    /// Creates an instance of <see cref="WorkerServiceFactory{TEntryPoint}"/>. This factory can be used to
    /// create a <see cref="TestServer"/> instance using the MVC application defined by <typeparamref name="TEntryPoint"/>
    /// and one or more <see cref="HttpClient"/> instances used to send <see cref="HttpRequestMessage"/> to the <see cref="TestServer"/>.
    /// The <see cref="WebApplicationFactory{TEntryPoint}"/> will find the entry point class of <typeparamref name="TEntryPoint"/>
    /// assembly and initialize the application by calling <c>IWebHostBuilder CreateWebHostBuilder(string [] args)</c>
    /// on <typeparamref name="TEntryPoint"/>.
    /// </para>
    /// <para>
    /// This constructor will infer the application content root path by searching for a
    /// <see cref="WorkerServiceFactoryContentRootAttribute"/> on the assembly containing the functional tests with
    /// a key equal to the <typeparamref name="TEntryPoint"/> assembly <see cref="Assembly.FullName"/>.
    /// In case an attribute with the right key can't be found, <see cref="WebApplicationFactory{TEntryPoint}"/>
    /// will fall back to searching for a solution file (*.sln) and then appending <typeparamref name="TEntryPoint"/> assembly name
    /// to the solution directory. The application root directory will be used to discover views and content files.
    /// </para>
    /// <para>
    /// The application assemblies will be loaded from the dependency context of the assembly containing
    /// <typeparamref name="TEntryPoint" />. This means that project dependencies of the assembly containing
    /// <typeparamref name="TEntryPoint" /> will be loaded as application assemblies.
    /// </para>
    /// </summary>
    public WorkerServiceFactory()
    {
        _configuration = ConfigureHost;
    }

    /// <summary>
    /// Finalizes an instance of the <see cref="WorkerServiceFactory{TEntryPoint}"/> class.
    /// </summary>
    ~WorkerServiceFactory()
    {
        Dispose(false);
    }

    /// <summary>
    /// Gets the <see cref="IServiceProvider"/> created by the server associated with this <see cref="WebApplicationFactory{TEntryPoint}"/>.
    /// </summary>
    public virtual IServiceProvider Services
    {
        get
        {
            EnsureStarted();
            return _host?.Services;
        }
    }

    /// <summary>
    /// Gets the <see cref="IReadOnlyList{WebApplicationFactory}"/> of factories created from this factory
    /// by further customizing the <see cref="IWebHostBuilder"/> when calling
    /// <see cref="WebApplicationFactory{TEntryPoint}.WithWebHostBuilder(Action{IWebHostBuilder})"/>.
    /// </summary>
    public IReadOnlyList<WorkerServiceFactory<TEntryPoint>> Factories => _derivedFactories.AsReadOnly();

    /// <summary>
    /// Creates a new <see cref="WebApplicationFactory{TEntryPoint}"/> with a <see cref="IWebHostBuilder"/>
    /// that is further customized by <paramref name="configuration"/>.
    /// </summary>
    /// <param name="configuration">
    /// An <see cref="Action{IWebHostBuilder}"/> to configure the <see cref="IWebHostBuilder"/>.
    /// </param>
    /// <returns>A new <see cref="WebApplicationFactory{TEntryPoint}"/>.</returns>
    public WorkerServiceFactory<TEntryPoint> WithHostBuilder(Action<IHostBuilder> configuration) =>
        WithHostBuilderCore(configuration);

    internal virtual WorkerServiceFactory<TEntryPoint> WithHostBuilderCore(Action<IHostBuilder> configuration)
    {
        var factory = new DelegatedWorkerFactory(
            CreateHost,
            CreateHostBuilder,
            GetTestAssemblies,
            builder =>
            {
                _configuration(builder);
                configuration(builder);
            });

        _derivedFactories.Add(factory);

        return factory;
    }

    public async Task Start()
    {
        EnsureDepsFile();

        var hostBuilder = CreateHostBuilder();
        SetContentRoot(hostBuilder);
        _configuration(hostBuilder);
        _host = CreateHost(hostBuilder);

        await _host.StartAsync();
    }

    public Task Stop()
    {
        Dispose();
        return Task.CompletedTask;
    }

    private void EnsureStarted()
    {
        if (_host == null)
        {
            throw new Exception("Worker service wasn't built. Call StartAsync() method");
        }
    }

    private void SetContentRoot(IHostBuilder builder)
    {
        var contentRoot = GetContentRootFromAssembly();

        if (contentRoot != null)
        {
            builder.UseContentRoot(contentRoot);
        }
        else
        {
            builder.UseSolutionRelativeContentRoot(typeof(TEntryPoint).Assembly.GetName().Name!);
        }
    }

    private static string? GetContentRootFromFile(string file)
    {
        var data = JsonSerializer.Deserialize<IDictionary<string, string>>(File.ReadAllBytes(file))!;
        var key = typeof(TEntryPoint).Assembly.GetName().FullName;

        // If the `ContentRoot` is not provided in the app manifest, then return null
        // and fallback to setting the content root relative to the entrypoint's assembly.
        if (!data.TryGetValue(key, out var contentRoot))
        {
            return null;
        }

        return (contentRoot == "~") ? AppContext.BaseDirectory : contentRoot;
    }

    private string? GetContentRootFromAssembly()
    {
        var metadataAttributes = GetContentRootMetadataAttributes(
            typeof(TEntryPoint).Assembly.FullName!,
            typeof(TEntryPoint).Assembly.GetName().Name!);

        string? contentRoot = null;
        for (var i = 0; i < metadataAttributes.Length; i++)
        {
            var contentRootAttribute = metadataAttributes[i];
            var contentRootCandidate = Path.Combine(
                AppContext.BaseDirectory,
                contentRootAttribute.ContentRootPath);

            var contentRootMarker = Path.Combine(
                contentRootCandidate,
                Path.GetFileName(contentRootAttribute.ContentRootTest));

            if (File.Exists(contentRootMarker))
            {
                contentRoot = contentRootCandidate;
                break;
            }
        }

        return contentRoot;
    }

    private WorkerServiceFactoryContentRootAttribute[] GetContentRootMetadataAttributes(
        string tEntryPointAssemblyFullName,
        string tEntryPointAssemblyName)
    {
        var testAssembly = GetTestAssemblies();
        var metadataAttributes = testAssembly
            .SelectMany(a => a.GetCustomAttributes<WorkerServiceFactoryContentRootAttribute>())
            .Where(a => string.Equals(a.Key, tEntryPointAssemblyFullName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a.Key, tEntryPointAssemblyName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Priority)
            .ToArray();

        return metadataAttributes;
    }

    /// <summary>
    /// Gets the assemblies containing the functional tests. The
    /// <see cref="WorkerServiceFactoryContentRootAttribute"/> applied to these
    /// assemblies defines the content root to use for the given
    /// <typeparamref name="TEntryPoint"/>.
    /// </summary>
    /// <returns>The list of <see cref="Assembly"/> containing tests.</returns>
    protected virtual IEnumerable<Assembly> GetTestAssemblies()
    {
        try
        {
            // The default dependency context will be populated in .net core applications.
            var context = DependencyContext.Default;
            if (context == null || context.CompileLibraries.Count == 0)
            {
                // The app domain friendly name will be populated in full framework.
                return new[] { Assembly.Load(AppDomain.CurrentDomain.FriendlyName) };
            }

            var runtimeProjectLibraries = context.RuntimeLibraries
                .ToDictionary(r => r.Name, r => r, StringComparer.Ordinal);

            // Find the list of projects
            var projects = context.CompileLibraries.Where(l => l.Type == "project");

            var entryPointAssemblyName = typeof(TEntryPoint).Assembly.GetName().Name;

            // Find the list of projects referencing TEntryPoint.
            var candidates = context.CompileLibraries
                .Where(library => library.Dependencies.Any(d => string.Equals(d.Name, entryPointAssemblyName, StringComparison.Ordinal)));

            var testAssemblies = new List<Assembly>();
            foreach (var candidate in candidates)
            {
                if (runtimeProjectLibraries.TryGetValue(candidate.Name, out var runtimeLibrary))
                {
                    var runtimeAssemblies = runtimeLibrary.GetDefaultAssemblyNames(context);
                    testAssemblies.AddRange(runtimeAssemblies.Select(Assembly.Load));
                }
            }

            return testAssemblies;
        }
        catch (Exception)
        {
        }

        return Array.Empty<Assembly>();
    }

    private static void EnsureDepsFile()
    {
        if (typeof(TEntryPoint).Assembly.EntryPoint == null)
        {
            throw new InvalidOperationException($"The provided Type '{typeof(TEntryPoint).Name}' does not belong to an assembly with an entry point. A common cause for this error is providing a Type from a class library.");
        }

        var depsFileName = $"{typeof(TEntryPoint).Assembly.GetName().Name}.deps.json";
        var depsFile = new FileInfo(Path.Combine(AppContext.BaseDirectory, depsFileName));

        if (!depsFile.Exists)
        {
            throw new InvalidOperationException(
                $"Can't find '{depsFile.FullName}'. This file is required for functional tests to run properly. There should be a copy of the file on your source project bin folder. If that is not the case, make sure that the property PreserveCompilationContext is set to true on your project file. E.g '<PreserveCompilationContext>true</PreserveCompilationContext>'. For functional tests to work they need to either run from the build output folder or the {Path.GetFileName(depsFile.FullName)} file from your application's output directory must be copied to the folder where the tests are running on. A common cause for this error is having shadow copying enabled when the tests run.");
        }
    }

    /// <summary>
    /// Creates a <see cref="IHostBuilder"/> used to set up <see cref="TestServer"/>.
    /// </summary>
    /// <remarks>
    /// The default implementation of this method looks for a <c>public static IHostBuilder CreateHostBuilder(string[] args)</c>
    /// method defined on the entry point of the assembly of <typeparamref name="TEntryPoint" /> and invokes it passing an empty string
    /// array as arguments.
    /// </remarks>
    /// <returns>A <see cref="IHostBuilder"/> instance.</returns>
    protected virtual IHostBuilder? CreateHostBuilder()
    {
        var hostBuilder = HostFactoryResolver.ResolveHostBuilderFactory<IHostBuilder>(typeof(TEntryPoint).Assembly)?.Invoke(Array.Empty<string>());

        hostBuilder?.UseEnvironment(Environments.Development);
        return hostBuilder;
    }

    /// <summary>
    /// Creates the <see cref="IHost"/> with the bootstrapped application in <paramref name="builder"/>.
    /// This is only called for applications using <see cref="IHostBuilder"/>. Applications based on
    /// <see cref="IWebHostBuilder"/> will use <see cref="CreateServer"/> instead.
    /// </summary>
    /// <param name="builder">The <see cref="IHostBuilder"/> used to create the host.</param>
    /// <returns>The <see cref="IHost"/> with the bootstrapped application.</returns>
    protected virtual IHost CreateHost(IHostBuilder builder)
    {
        var host = builder.Build();
        //host.Start();
        return host;
    }

    /// <summary>
    /// Gives a fixture an opportunity to configure the application before it gets built.
    /// </summary>
    /// <param name="builder">The <see cref="IWebHostBuilder"/> for the application.</param>
    protected virtual void ConfigureHost(IHostBuilder builder)
    {
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
    /// </summary>
    /// <param name="disposing">
    /// <see langword="true" /> to release both managed and unmanaged resources;
    /// <see langword="false" /> to release only unmanaged resources.
    /// </param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            if (!_disposedAsync)
            {
                DisposeAsync()
                    .AsTask()
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
            }
        }

        _disposed = true;
    }

    /// <inheritdoc />
    public virtual async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_disposedAsync)
        {
            return;
        }

        foreach (var factory in _derivedFactories)
        {
            await ((IAsyncDisposable)factory).DisposeAsync().ConfigureAwait(false);
        }

        if (_host != null)
        {
            await _host.StopAsync().ConfigureAwait(false);
            _host?.Dispose();
        }

        _disposedAsync = true;

        Dispose(disposing: true);

        GC.SuppressFinalize(this);
    }

    private sealed class DelegatedWorkerFactory : WorkerServiceFactory<TEntryPoint>
    {
        private readonly Func<IHostBuilder, IHost> _createHost;
        private readonly Func<IHostBuilder?> _createHostBuilder;
        private readonly Func<IEnumerable<Assembly>> _getTestAssemblies;

        public DelegatedWorkerFactory(
            Func<IHostBuilder, IHost> createHost,
            Func<IHostBuilder?> createHostBuilder,
            Func<IEnumerable<Assembly>> getTestAssemblies,
            Action<IHostBuilder> configureHost)
        {
            _createHost = createHost;
            _createHostBuilder = createHostBuilder;
            _getTestAssemblies = getTestAssemblies;
            _configuration = configureHost;
        }

        protected override IHost CreateHost(IHostBuilder builder) => _createHost(builder);

        protected override IHostBuilder? CreateHostBuilder() => _createHostBuilder();

        protected override IEnumerable<Assembly> GetTestAssemblies() => _getTestAssemblies();

        protected override void ConfigureHost(IHostBuilder builder) => _configuration(builder);

        internal override WorkerServiceFactory<TEntryPoint> WithHostBuilderCore(Action<IHostBuilder> configuration)
        {
            return new DelegatedWorkerFactory(
                _createHost,
                _createHostBuilder,
                _getTestAssemblies,
                builder =>
                {
                    _configuration(builder);
                    configuration(builder);
                });
        }
    }
}