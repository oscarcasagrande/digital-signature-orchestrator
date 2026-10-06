import type { JournalEvent } from '../api/types';
import { actorLabel, eventLabel, formatTime } from '../util/format';

/** Human-readable view of the event journal: time and description, oldest first. */
export function Timeline({ events }: { events: JournalEvent[] }) {
  if (events.length === 0) return <p className="empty">No events yet.</p>;
  return (
    <ol className="timeline" aria-label="Process timeline">
      {events.map((e) => (
        <li key={e.eventId}>
          <time dateTime={e.timestamp}>{formatTime(e.timestamp)}</time>
          <span className="timeline-text">{eventLabel(e.type)}</span>
          <span className="timeline-actor">{actorLabel(e.actor)}</span>
        </li>
      ))}
    </ol>
  );
}
