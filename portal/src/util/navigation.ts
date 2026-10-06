/** Starts a browser download. Kept in its own module so tests can replace it. */
export function openDownload(url: string): void {
  window.location.assign(url);
}

/** Full-page redirect (used to reach the identity provider). Replaceable in tests. */
export function redirectTo(url: string): void {
  window.location.assign(url);
}
