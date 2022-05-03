using System;
using System.Threading;
using System.Threading.Tasks;
using NHibernate;

namespace LiquidProjections.NHibernate
{
    /// <summary>
    /// Supports sharing of <see cref="ISession"/> and <see cref="ITransaction"/> over multiple contexts.
    /// </summary>
    public class SessionState : IDisposable
    {
        private readonly Func<ISession> _sessionFactory;
        private ISession _session;
        private ITransaction _transaction;
        private bool _createdSession;
        private bool _createdTransaction;
        private bool _disposedValue;

        /// <summary>
        /// Create a new <see cref="SessionState"/>.
        /// </summary>
        /// <remarks>
        /// <see cref="Session"/> and <see cref="Transaction"/> will be created and disposed with this <see cref="SessionState"/>.
        /// </remarks>
        /// <param name="sessionFactory">A factory method for creating sessions when required.</param>
        public SessionState(Func<ISession> sessionFactory)
        {
            _sessionFactory = sessionFactory;

            CreateSessionAndTransaction();
        }

        /// <summary>
        /// Create a new <see cref="SessionState"/> with an existing <see cref="ISession"/>.
        /// </summary>
        /// <remarks>
        /// <see cref="Session"/> and <see cref="Transaction"/> will be re-used from the supplied <paramref name="session"/>.
        /// If they were <c>null</c> or inactive, or if <see cref="AlwaysDispose"/> is set to <c>true</c>, then they will
        /// also be disposed with this <see cref="SessionState"/>.
        /// </remarks>
        /// <param name="sessionFactory">A factory method for creating sessions when required.</param>
        /// <param name="session">An existing <see cref="ISession"/> to use.</param>
        public SessionState(Func<ISession> sessionFactory, ISession session)
        {
            _sessionFactory = sessionFactory;
            _session = session;

            CreateSessionAndTransaction();
        }

        /// <summary>
        /// The managed <see cref="ISession"/>.
        /// </summary>
        public ISession Session
        {
            get { return _session; }
        }

        /// <summary>
        /// The managed <see cref="ITransaction"/>.
        /// </summary>
        public ITransaction Transaction
        {
            get { return _transaction; }
        }

        /// <summary>
        /// Indicates if the <see cref="Session"/> was created by this <see cref="SessionState"/>.
        /// </summary>
        public bool CreatedSession
        {
            get { return _createdSession; }
        }

        /// <summary>
        /// Indicates if the <see cref="Transaction"/> was created by this <see cref="SessionState"/>.
        /// </summary>
        public bool CreatedTransaction
        {
            get { return _createdTransaction; }
        }

        /// <summary>
        /// Set to <c>true</c> to force disposal of the <see cref="Session"/> and <see cref="Transaction"/> when this
        /// <see cref="SessionState"/> is disposed, irrelevant of whether this <see cref="SessionState"/> created them.
        /// </summary>
        public bool AlwaysDispose { get; set; }

        /// <summary>
        /// Commit the <see cref="Transaction"/>, but only if it was created by this <see cref="SessionState"/>.
        /// </summary>
        public Task CommitIfMine(CancellationToken ct)
        {
            if (!CreatedTransaction)
            {
                return Task.CompletedTask;
            }

            return _transaction.CommitAsync(ct);
        }

        /// <summary>
        /// Commit the <see cref="Transaction"/>, but only if it was created by this <see cref="SessionState"/>.
        /// </summary>
        public void CommitIfMine()
        {
            if (!CreatedTransaction)
            {
                return;
            }

            _transaction.Commit();
        }

        private void CreateSessionAndTransaction()
        {
            if (_session?.IsOpen != true || _session?.IsConnected != true)
            {
                _session = _sessionFactory();
                _createdSession = true;
            }

            _transaction = _session.GetCurrentTransaction();
            if (_transaction?.IsActive != true)
            {
                _transaction = _session.BeginTransaction();
                _createdTransaction = true;
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    if (CreatedTransaction || AlwaysDispose)
                    {
                        if (_transaction.IsActive)
                        {
                            _transaction.Rollback();
                        }
                        _transaction?.Dispose();
                    }

                    if (CreatedSession || AlwaysDispose)
                    {
                        _session?.Dispose();
                    }
                }

                _disposedValue = true;
            }
        }

        /// <summary>
        /// Disposes this <see cref="SessionState"/>.
        /// </summary>
        /// <remarks>
        /// If <see cref="Session"/> or <see cref="Transaction"/> were created by this <see cref="SessionState"/>,
        /// then they will also be disposed.
        /// </remarks>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
