using NHibernate;

namespace LiquidProjections.NHibernate
{
    public interface ITrackSkippedEvents
    {
        bool WasSkipped { get; set; }
    }

    public sealed class NHibernateProjectionContext : ProjectionContext, ITrackSkippedEvents
    {
        private bool wasHandled;

        public ISession Session { get; set; }

        /// <summary>
        /// Indicates that at least one event in the current batch was mapped in the event map and thus was handled by the
        /// projector.
        /// </summary>
        internal bool WasHandled
        {
            get => wasHandled;
            set => wasHandled |= value;
        }

        /// <summary>
        /// Indicates if an event was skipped by a When clause which should treat the event as unhandled.
        /// </summary>
        public bool WasSkipped { get; set; }
    }
}