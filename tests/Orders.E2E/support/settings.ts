/** Everything the suite reads from the environment. */
const mode = (process.env.E2E_MODE ?? 'fake').toLowerCase();
if (mode !== 'fake' && mode !== 'live') {
  throw new Error(`E2E_MODE must be "fake" or "live", not "${mode}".`);
}

export const settings = {
  baseURL: (process.env.CONSOLE_URL ?? 'http://localhost:8088').replace(/\/+$/, ''),
  token: process.env.CONSOLE_VERIFICATION_TOKEN || undefined,
  mode: mode as 'fake' | 'live',
  live: mode === 'live',
  /** The longest a single step may take before the test gives up. */
  stepTimeoutMs: mode === 'live' ? 25 * 60 * 1000 : 60 * 1000,
  /** How often to look at the step's state. */
  pollMs: mode === 'live' ? 5_000 : 250,
};
