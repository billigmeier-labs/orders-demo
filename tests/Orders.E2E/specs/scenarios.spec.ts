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

test('the scoreboard shows every number against its target, saves the run and compares past runs', async ({ page, request }) => {
  const scenario = await getScenario(request, 'scoreboard');
  await resetCurrentRun(request);
  await page.goto('/present?scenario=scoreboard');

  await driveStep(page, 'scoreboard', scenario.steps[0]);
  await page.goto('/present?scenario=scoreboard&view=show');
  const board = page.getByTestId('scoreboard');
  await expect(board).toBeVisible();
  for (const id of ['ci-p90', 'pr-size', 'pr-to-merge', 'merge-to-prod', 'change-failure', 'agent-clean', 'review-time', 'mutation']) {
    const row = board.locator(`[data-score-row="${id}"]`);
    await expect(row, id).toBeVisible();
    await expect(row.locator('.pill'), `${id} verdict`).toHaveText(/^(met|missed|no data)$/);
  }
  for (const id of ['ci-p50', 'queue-wait', 'deploys', 'rollbacks', 'flaky', 'run-cost', 'cost']) {
    await expect(board.locator(`[data-score-row="${id}"]`), id).toBeVisible();
  }
  await expect(board.getByTestId('ci-jobs').locator('[data-ci-job]').first()).toBeVisible();
  if (!settings.live) {
    await expect(board.getByTestId('scoreboard-saved')).toContainText('runs/');
  }

  await page.goto('/present?scenario=scoreboard');
  await driveStep(page, 'scoreboard', scenario.steps[1]);
  await page.goto('/present?scenario=scoreboard&view=past');
  await expect(page.getByTestId('past-runs').locator('[data-past-run]').first()).toBeVisible();

  await page.goto('/present?scenario=scoreboard');
  await driveStep(page, 'scoreboard', scenario.steps[2]);
  await page.goto('/present?scenario=scoreboard&view=teardown');
  await expect(page.getByTestId('step-command')).toHaveText('just env down');
  if (!settings.live) {
    await expect(page.getByTestId('step-output-teardown')).toContainText('runs/');
  }

  await page.goto('/present?scenario=scoreboard');
  await resetFromPage(page);
});
