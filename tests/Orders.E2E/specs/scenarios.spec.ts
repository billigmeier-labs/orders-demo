import { expect, test } from '@playwright/test';
import { settings } from '../support/settings';
import { driveStep, getScenario, resetCurrentRun, resetFromPage } from '../support/run';

interface LibraryEntry {
  id: string;
  /** Takes many minutes against the real GitHub and AWS (deploys, alarms, agent PRs). */
  slow: boolean;
  /** Needs a person to do something in GitHub mid-run, so it only runs against the fakes. */
  needsPerson?: string;
}

// The scenario library in order (Scenarios/Definitions, by "order"). The first test checks the console agrees,
// so a new scenario file fails the suite until it is listed here.
const library: LibraryEntry[] = [
  { id: 'bad-deploy', slow: true },
  { id: 'incident', slow: true },
  { id: 'guardrails', slow: false },
  { id: 'risky-change', slow: true, needsPerson: 'the presenter assigns the issue to the agent in GitHub' },
  { id: 'mutation', slow: false },
  { id: 'scoreboard', slow: false },
];

test('the library lists every scenario', async ({ page }) => {
  await page.goto('/present');
  const links = page.locator('[data-testid=scenario-library] [data-scenario]');
  await expect(links).toHaveCount(library.length + 1);
  const ids = await links.evaluateAll(els => els.map(e => e.getAttribute('data-scenario')));
  expect(ids).toEqual(['tour', ...library.map(s => s.id)]);
});

for (const entry of library) {
  const tag = entry.slow ? ['@live-slow'] : [];
  test(`scenario ${entry.id} runs end to end`, { tag }, async ({ page, request }) => {
    test.skip(settings.live && !!entry.needsPerson, `Needs a person: ${entry.needsPerson}.`);

    const scenario = await getScenario(request, entry.id);
    await resetCurrentRun(request);

    await page.goto('/present');
    await page.locator(`[data-testid=scenario-library] [data-scenario="${entry.id}"]`).click();
    await expect(page).toHaveURL(new RegExp(`scenario=${entry.id}`));
    await expect(page.getByTestId('run-live')).toHaveAttribute('data-scenario-id', entry.id);
    await expect(page.getByTestId('run')).toHaveAttribute('data-run-state', 'idle');
    await expect(page.getByTestId('run-locked')).toHaveCount(0);

    for (const step of scenario.steps) {
      await test.step(`${step.id}: ${step.title}`, () => driveStep(page, entry.id, step));
    }

    await expect(page.getByTestId('run')).toHaveAttribute('data-run-state', 'completed');
    await resetFromPage(page);
  });
}
