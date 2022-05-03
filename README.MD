# Liquid Projections for NHibernate [![Build status](https://ci.appveyor.com/api/projects/status/8shaimqx3u366bji/branch/master?svg=true)](https://ci.appveyor.com/project/dennisdoomen/liquidprojections-nhibernate-69l6u/branch/master)

## What is this?
This [package](https://www.nuget.org/packages/LiquidProjections.NHibernate/) provides an `NHibernateProjector` that allows you to build [NHibernate]((https://www.nuget.org/packages/NHibernate/))-backed projectors for use with [LiquidProjections'](https://github.com/liquidprojections/LiquidProjections) event mapping API and `Dispatcher` class.

## What does it offer?
* Supports NHibernate 4.0 or later. 
* Allows customizing how many LiquidProjections [Transaction](https://github.com/liquidprojections/LiquidProjections/blob/master/Src/LiquidProjections.Abstractions/Transaction.cs)s it should process in a single database transaction.
* Supports nesting one or more `NHibernateChildProjector`s that can be used for maintaining lookup tables and which are part of the same database transaction as the parent projector creates.
* Ensures autonomy by storing the  `IProjectionState` (e.g. the last transaction checkpoint) in the same transaction as the actual projection data.
* Allows the projector to be configured with an `ExceptionPolicy` delegate so that you can handle projection exceptions and get full control on whether or not the projector should retry the entire batch, individual transactions, ignore or abort the exception.
* If the implementation of `IProjectorStat`e has additional custom properties, you can use the `EnrichState` delegate of the `NHibernateProjector` to update the state object before it is written to the database. This is called as part of the same database transaction that encompasses the projector code.

## Why would I use NHibernate for something like a projection
Some have argued that using raw SQL is the fasted method for updating  RDBMS-backed projections. And quite often this is true. But depending on the nature of the projection, in particular when it touches the same projection many times in the same batch of events, the unit-of-work (the `ISession`) can provide some significant speed improvements.
