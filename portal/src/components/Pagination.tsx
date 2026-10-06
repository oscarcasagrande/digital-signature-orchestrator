interface Props {
  page: number;
  pageSize: number;
  total: number;
  noun: string;
  onChange: (page: number) => void;
}

export function Pagination({ page, pageSize, total, noun, onChange }: Props) {
  const pages = Math.max(1, Math.ceil(total / pageSize));
  return (
    <nav className="pagination" aria-label="Pagination">
      <button type="button" onClick={() => onChange(page - 1)} disabled={page <= 1}>
        Previous
      </button>
      <span aria-live="polite">
        Page {page} of {pages} · {total} {noun}
      </span>
      <button type="button" onClick={() => onChange(page + 1)} disabled={page >= pages}>
        Next
      </button>
    </nav>
  );
}
