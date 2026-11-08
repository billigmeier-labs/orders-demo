import { expect, type APIRequestContext, type Page } from '@playwright/test';
import { settings } from './settings';

export interface ScenarioStep {
  id: string;
  title: string;
  action: string | null;
  timeoutSeconds: number;
}

export interface Scenario {
  id: string;
  title: string;
  summary: string;
  tour: boolean;
  steps: ScenarioStep[];
}

const actionHeaders = { 'X-Console-Action': '1' };

/** The scenario definitions as the console loaded them. */
export async function getScenario(request: APIRequestContext, id: string): Promise<Scenario> {
  const res = await request.get('/api/scenarios');
  expect(res.status(), 'GET /api/scenarios').toBe(200);
  const all = (await res.json()) as Scenario[];
  const found = all.find(s => s.id === id);
  if (!found) throw new Error(`The console has no scenario "${id}" (it has ${all.map(s => s.id).join(', ')}).`);
  return found;
}

/** Resets whatever run the console holds, so a spec starts from a clean slate. */
export async function resetCurrentRun(request: APIRequestContext): Promise<void> {
  const res = await request.get('/api/run');
  expect(res.status(), 'GET /api/run').toBe(200);
  const { run } = (await res.json()) as { run: { scenarioId: string } | null };
  if (run) {
    const reset = await request.post(`/api/scenarios/${run.scenarioId}/reset`, { headers: actionHeaders });
    expect(reset.status(), `reset ${run.scenarioId}`).toBe(200);
  }
}

export const stepRow = (page: Page, stepId: string) =>
  page.locator(`[data-testid=steps] [data-step="${stepId}"]`);

const stepState = (page: Page, stepId: string) => stepRow(page, stepId).getAttribute('data-step-state');

async function waitForState(page: Page, stepId: string, wanted: RegExp, deadline: number): Promise<string> {
  let last = '';
  await expect
    .poll(async () => (last = (await stepState(page, stepId)) ?? ''), {
      message: `step "${stepId}" never reached ${wanted}`,
      timeout: Math.max(1_000, deadline - Date.now()),
      intervals: [settings.pollMs],
    })
    .toMatch(wanted);
  return last;
}

async function failureText(page: Page, stepId: string): Promise<string> {
  const output = page.locator(`[data-testid=step-output-${stepId}]`);
  return (await output.count()) ? ((await output.innerText()) ?? '').trim() : '(no output)';
}

/**
 * Drives one step the way the presenter does: presses its action button when it is ready, then waits for it
 * to be done. A timed-out step gets "Keep waiting" in live mode (the real thing can be slow) until the
 * suite's own per-step budget runs out; in fake mode it fails straight away.
 */
export async function driveStep(page: Page, scenarioId: string, step: ScenarioStep): Promise<void> {
  const deadline = Date.now() + settings.stepTimeoutMs;
  let pressed = false;
  for (;;) {
    const state = await waitForState(page, step.id, pressed || !step.action ? /^(done|failed|timedout)$/ : /^(ready|done|failed|timedout)$/, deadline);
    switch (state) {
      case 'done':
        return;
      case 'ready': {
        const button = page.locator(`[data-testid=step-action][data-step-action="${step.id}"]`);
        await expect(button, `action button for "${step.id}"`).toHaveAttribute('id', `action-${scenarioId}-${step.id}`);
        await button.click();
        pressed = true;
        break;
      }
      case 'timedout': {
        if (!settings.live || Date.now() >= deadline) {
          throw new Error(`Step "${step.id}" timed out: ${await failureText(page, step.id)}`);
        }
        await page.locator(`[data-testid=step-extend][data-step-extend="${step.id}"]`).click();
        await waitForState(page, step.id, /^(waiting|done|failed)$/, deadline);
        break;
      }
      default:
        throw new Error(`Step "${step.id}" ${state}: ${await failureText(page, step.id)}`);
    }
  }
}

/** Presses Reset scenario and accepts the confirmation, as the presenter does. */
export async function resetFromPage(page: Page): Promise<void> {
  page.once('dialog', d => void d.accept());
  await page.getByTestId('scenario-reset').click();
  await expect(page.getByTestId('run')).toHaveAttribute('data-run-state', 'idle');
}
