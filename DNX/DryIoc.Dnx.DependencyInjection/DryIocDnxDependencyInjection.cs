﻿/*
The MIT License (MIT)

Copyright (c) 2015 Maksim Volkau

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace DryIoc.Dnx.DependencyInjection
{
    /// <summary>Provides populating of service collection.</summary>
    public static class DryIocDnxDependencyInjection
    {
        /// <summary>Scope name for singletons. The root scope name which is implicitly opened for DI.</summary>
        public static string SingletonsScopeName = "root";

        /// <summary>Entry method with all DI adaptations applied which returns configured DryIoc container wrapped in <see cref="IServiceProvider"/>
        /// populated from <paramref name="services"/>.</summary>
        /// <param name="container">DryIoc container to adapt. Passed container will stay intact, instead the new adapted container instance will be
        /// returned.</param>
        /// <param name="services">Service collection to register into container.</param>
        /// <returns>New adapted container instance.</returns>
        public static IServiceProvider GetDryIocServiceProvider(this IContainer container, IServiceCollection services)
        {
            var adapter = container.WithDependencyInjectionAdapter();

            adapter.Populate(services);

            return adapter.Resolve<IServiceProvider>();
        }

        /// <summary>Creates new container from the <paramref name="container"/> adapted to be used
        /// with Asp.Net 5 dependency injection:
        /// - First method sets the rules specific for Asp.Net DI.
        /// - Then registers transient disposables tracker.
        /// - Then registers DryIoc implementations of <see cref="IServiceProvider"/> and <see cref="IServiceScopeFactory"/>.
        /// - Then opens top scope because AspNet DI assumes that the scoped registration may be resolve without explicit scope creation,
        /// that means the root scope is explicitly available.
        /// Plus registers DryIoc implementations of <see cref="IServiceProvider"/> and <see cref="IServiceScopeFactory"/>.
        /// </summary>
        /// <param name="container">Source container to adapt.</param>
        /// <returns>New container with modified rules.</returns>
        public static IContainer WithDependencyInjectionAdapter(this IContainer container)
        {
            if (container.ScopeContext != null)
                throw new ArgumentException("Adapted container uses ambient scope context which is not supported by AspNet DI.");

            var adapter = container.With(rules => rules
                .With(FactoryMethod.ConstructorWithResolvableArguments)
                .WithFactorySelector(Rules.SelectLastRegisteredFactory())
                .WithoutThrowOnRegisteringDisposableTransient());

            // Tracks disposables in current scope
            // todo: disposable transient dependency of singleton is not supported
            adapter.RegisterTransientDisposablesTracker();

            // make container available for injection as a scoped resolver
            adapter.RegisterDelegate(resolver => (IContainer)resolver);

            adapter.Register<IServiceProvider, DryIocServiceProvider>(Reuse.InCurrentScope);

            // factory should be scoped to enable nested scopes creation using outer scope factory
            adapter.Register<IServiceScopeFactory, DryIocServiceScopeFactory>(Reuse.InCurrentScope);

            return adapter.OpenScope(SingletonsScopeName);
        }

        /// <summary>Registers descriptors services into container and that's all. May be called multiple times with
        /// different service collections.</summary>
        /// <param name="container">The container.</param>
        /// <param name="descriptors">The service descriptors.</param>
        public static void Populate(this IContainer container, IEnumerable<ServiceDescriptor> descriptors)
        {
            foreach (var descriptor in descriptors)
                container.RegisterDescriptor(descriptor);
        }

        /// <summary>Registers described service into container by mapping DI Lifetime to DryIoc Reuse, 
        /// and DI registration type to corresponding DryIoc Register, RegisterDelegate or RegisterInstance.</summary>
        /// <param name="container">The container.</param>
        /// <param name="descriptor">Service descriptor.</param>
        public static void RegisterDescriptor(this IContainer container, ServiceDescriptor descriptor)
        {
            var reuse = ConvertLifetimeToReuse(descriptor.Lifetime);

            if (descriptor.ImplementationType != null)
            {
                container.Register(descriptor.ServiceType, descriptor.ImplementationType, reuse);
            }
            else if (descriptor.ImplementationFactory != null)
            {
                container.RegisterDelegate(descriptor.ServiceType,
                    r => descriptor.ImplementationFactory(r.Resolve<IServiceProvider>()), 
                    reuse);
            }
            else
            {
                container.RegisterInstance(descriptor.ServiceType, descriptor.ImplementationInstance, reuse);
            }
        }

        private static IReuse ConvertLifetimeToReuse(ServiceLifetime lifetime)
        {
            switch (lifetime)
            {
                case ServiceLifetime.Singleton:
                    return Reuse.InCurrentNamedScope(SingletonsScopeName);
                case ServiceLifetime.Scoped:
                    return Reuse.InCurrentScope;
                case ServiceLifetime.Transient:
                    return Reuse.Transient;
                default:
                    throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, "Not supported lifetime");
            }
        }
    }

    /// <summary>Delegates service resolution to wrapped DryIoc scoped container.</summary>
    /// <remarks>When disposed, disposed scoped container: that means the singletons registered directly to DryIoc container won't
    /// be disposed. You should take care for them yourself outside of DI.</remarks>
    public sealed class DryIocServiceProvider : IServiceProvider, IDisposable
    {
        private readonly IContainer _scopedContainer;

        /// <summary>Wraps passed container.</summary> <param name="scopedContainer">subj.</param>
        public DryIocServiceProvider(IContainer scopedContainer)
        {
            _scopedContainer = scopedContainer;
        }

        /// <summary>Delegates resolution to scoped container. Uses <see cref="IfUnresolved.ReturnDefault"/> policy to return
        /// default value in case of resolution errors.</summary>
        /// <param name="serviceType">Registered type to resolve.</param>
        /// <returns>Resolved service object.</returns>
        public object GetService(Type serviceType)
        {
            return _scopedContainer.Resolve(serviceType, ifUnresolvedReturnDefault: true);
        }

        /// <summary>Disposes scoped container: which in order disposes open scope.</summary>
        public void Dispose()
        {
            _scopedContainer.Dispose();
        }
    }

    /// <summary>The goal of the factory is create/open new scope.
    /// Factory by itself is scoped (not a singleton): that means you need resolve factory from outer scope to create nested scope.</summary>
    public sealed class DryIocServiceScopeFactory : IServiceScopeFactory
    {
        private readonly IContainer _scopedContainer;

        /// <summary>Creates factory and stores injected scoped container to use it for opening scopes.</summary>
        /// <param name="scopedContainer">Outer scoped container to be used to create nested scopes.</param>
        public DryIocServiceScopeFactory(IContainer scopedContainer)
        {
            _scopedContainer = scopedContainer;
        }

        /// <summary>Opens scope and wraps it into DI <see cref="IServiceScope"/> interface.</summary>
        /// <returns>DI wrapper of opened scope.</returns>
        public IServiceScope CreateScope()
        {
            var scope = _scopedContainer.OpenScope();
            return new DryIocServiceScope(scope.Resolve<IServiceProvider>());
        }

        private sealed class DryIocServiceScope : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; }

            public DryIocServiceScope(IServiceProvider serviceProvider)
            {
                ServiceProvider = serviceProvider;
            }

            public void Dispose() => (ServiceProvider as IDisposable)?.Dispose();
        }
    }
}

namespace DryIoc
{
    /// <summary>Tool to allow disposable transients tracking, which is not supported by DryIoc by default.</summary>
    public static class TrackTransientDisposables
    {
        /// <summary>Registers tracker in specified reuse scope + registers initializer for any object, which will
        /// add disposable object to the tracker.</summary>
        /// <param name="registrator">Container to use.</param>
        public static void RegisterTransientDisposablesTracker(this IRegistrator registrator)
        {
            registrator.Register<TransientDisposablesTracker>(Reuse.InCurrentScope);

            registrator.RegisterInitializer<object>(
                (service, r) => r.Resolve<TransientDisposablesTracker>().Track((IDisposable)service),
                request => request.ReuseLifespan == 0 
                    && (request.ImplementationType ?? request.GetActualServiceType()).IsAssignableTo(typeof(IDisposable)));
        }

        /// <summary>Tracker stores disposable objects, and disposes of them on its own Dispose.
        /// Actually it is a composite disposable.</summary>
        public sealed class TransientDisposablesTracker : IDisposable
        {
            private readonly Ref<IDisposable[]> _items = Ref.Of(ArrayTools.Empty<IDisposable>());

            /// <summary>Adds <paramref name="disposable"/> to tracked items which later will be disposed.</summary>
            /// <param name="disposable">Item to track.</param>
            public void Track(IDisposable disposable)
            {
                _items.Swap(i => i.AppendOrUpdate(disposable));
            }

            /// <summary>Disposes of the tracked items.</summary>
            public void Dispose()
            {
                var items = _items.Value;
                if (items.IsNullOrEmpty()) return;
                for (var i = 0; i < items.Length; i++)
                    items[i].Dispose();
                _items.Swap(_ => ArrayTools.Empty<IDisposable>());
            }
        }
    }
}