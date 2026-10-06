export function JsonView({ value, label }: { value: unknown; label: string }) {
  return (
    <pre className="json" tabIndex={0} aria-label={label}>
      {JSON.stringify(value, null, 2)}
    </pre>
  );
}
