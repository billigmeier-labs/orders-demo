import { expect, test } from '@playwright/test';
import { driveStep, getScenario, resetCurrentRun, resetFromPage, stepRow } from '../support/run';

// "Where this is running": the nodes each Tour stage highlights (Content/TourContent.cs).
const allNodes = ['repo', 'runner', 'queue', 'ecr', 'ecs', 'rds', 'cw', 'claude'];
const highlighted: Record<string, string[]> = {
  issue: ['repo'],
  triage: ['repo', 'runner', 'claude'],
  agent: ['repo', 'runner', 'claude'],
  checks: ['repo', 'runner', 'ecr', 'ecs', 'rds'],
  queue: ['repo', 'runner', 'queue'],
  deploy: ['runner', 'ecr', 'ecs', 'rds'],
  monitor: ['ecs', 'cw', 'runner', 'claude', 'repo'],
};

// Every Tour step is a lifecycle stage with the same id; the deploy and merge steps take many minutes live.
test('the Tour runs from a new issue to production @live-slow', async ({ page, context, request }) => {
  const tour = await getScenario(request, 'tour');
  expect(tour.tour).toBe(true);
  expect(tour.steps.map(s => s.id)).toEqual(Object.keys(highlighted));
  await resetCurrentRun(request);

  await page.goto('/');
  await page.getByTestId('nav-present').click();
  await expect(page.getByTestId('nav-present')).toHaveAttribute('aria-current', 'page');
  await expect(page.locator('[data-testid=scenario-library] [data-scenario=tour]')).toHaveAttribute('aria-current', 'page');
  await expect(page.getByTestId('run')).toHaveAttribute('data-run-state', 'idle');
  await expect(stepRow(page, 'issue')).toHaveAttribute('data-step-state', 'ready');

  // A second tab looks at each step while the presenter's tab follows the run.
  const inspector = await context.newPage();

  for (const step of tour.steps) {
    await test.step(`${step.id}: ${step.title}`, async () => {
      await driveStep(page, 'tour', step);
      await expect(page.locator(`[data-stage=${step.id}]`)).toHaveAttribute('data-stage-state', 'done');

      await inspector.goto(`/present?scenario=tour&view=${step.id}`);
      await expect(inspector.getByTestId('narration')).toHaveAttribute('data-step', step.id);

      const where = inspector.getByTestId('where-running');
      await expect(where).toBeVisible();
      for (const node of allNodes) {
        const on = highlighted[step.id].includes(node);
        await expect(where.locator(`[data-node=${node}]`), `${node} in "${step.id}"`).toHaveAttribute('data-node-on', String(on));
      }

      await inspector.getByTestId('explain-current').click();
      const drawer = inspector.getByTestId('explainer-drawer');
      await expect(drawer).toHaveAttribute('data-open', 'true');
      await expect(drawer).toHaveAttribute('data-explain-id', step.id);
      await expect(inspector.getByTestId('explainer-title')).not.toBeEmpty();
      await expect(inspector.getByTestId('explainer-what')).not.toBeEmpty();
      await expect(inspector.getByTestId('explainer-azure')).toContainText(/\S/);
      await inspector.keyboard.press('Escape');
      await expect(drawer).toHaveAttribute('data-open', 'false');
    });
  }

  await expect(page.getByTestId('run')).toHaveAttribute('data-run-state', 'completed');
  await expect(page.getByTestId('run-issue')).toBeVisible();
  await expect(page.getByTestId('run-pr')).toBeVisible();

  await resetFromPage(page);
  await inspector.close();
});
