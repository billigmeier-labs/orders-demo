import { expect, test } from '@playwright/test';
import { driveStep, getScenario, resetCurrentRun, resetFromPage, stepRow } from '../support/run';

// "Where this is running": the nodes each Tour stage highlights (Content/TourContent.cs).
const allNodes = ['repo', 'runner', 'queue', 'ecr', 'ecs', 'rds', 'cw', 'claude'];
const highlighted: Record<string, string[]> = {
  issue: ['repo'],
  triage: ['repo', 'runner', 'claude'],
  agent: ['repo', 'runner', 'claude'],
  checks: ['repo', 'runner', 'claude'],
  preview: ['runner', 'ecr', 'ecs', 'rds'],
  queue: ['repo', 'runner', 'queue'],
  staging: ['runner', 'ecr', 'ecs', 'rds'],
  production: ['runner', 'ecr', 'ecs', 'rds', 'cw'],
  traffic: ['ecs', 'cw'],
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
      // The live-traffic step shows production's requests arriving.
      if (step.id === 'traffic') {
        const prod = inspector.getByTestId('live-traffic').getByTestId('traffic-prod');
        await expect(prod).toBeVisible();
        expect(Number(await prod.getAttribute('data-latest-requests'))).toBeGreaterThanOrEqual(100);
        await expect(prod.getByTestId('traffic-rate')).toHaveText(/\/min$/);
      }

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
  // The header's step counter follows the run without a page reload.
  await expect(page.locator('[data-header-step]')).toHaveText(`Step ${tour.steps.length} of ${tour.steps.length}`);
  await expect(page.getByTestId('run-issue')).toBeVisible();
  await expect(page.getByTestId('run-pr')).toBeVisible();

  await resetFromPage(page);
  await inspector.close();
});
