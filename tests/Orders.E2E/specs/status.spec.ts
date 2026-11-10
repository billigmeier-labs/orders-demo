import { expect, test } from '@playwright/test';
import { settings } from '../support/settings';

test('Status renders health, alarms, GitHub activity, flags and cost', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByTestId('nav-status')).toHaveAttribute('aria-current', 'page');
  await expect(page.getByTestId('identity')).toHaveText('automation');
  await expect(page.locator('body')).toHaveAttribute('data-fakes', String(!settings.live));
  await expect(page.getByTestId('fake-mode')).toHaveCount(settings.live ? 0 : 1);

  // The live region refreshes itself; wait for one refresh so we test what the poller renders too.
  await expect(page.getByTestId('status-live')).toHaveAttribute('data-poll-at', /.+/, { timeout: 30_000 });

  for (const name of ['health', 'alarms', 'github', 'flags', 'cost', 'deploys', 'load']) {
    const panel = page.locator(`[data-panel=${name}]`);
    await expect(panel, `${name} panel`).toBeVisible();
    await expect(panel.getByTestId('panel-error'), `${name} panel error`).toHaveCount(0);
  }

  const health = page.locator('[data-panel=health]');
  for (const env of ['prod', 'staging']) {
    const card = health.locator(`[data-env=${env}]`);
    await expect(card, `${env} health`).toBeVisible();
    await expect(card).toHaveAttribute('data-health', 'ready');
    await expect(card.getByTestId('env-version')).not.toHaveText(/^(unknown)?$/);
    // Error rate and latency from the load balancer, per environment.
    const traffic = card.getByTestId(`traffic-${env}`);
    await expect(traffic, `${env} traffic`).toBeVisible();
    await expect(traffic.getByTestId('traffic-rate')).toHaveText(/^[\d,.]+\/min$/);
    await expect(traffic.getByTestId('traffic-errors')).toHaveText(/^[\d.]+%$/);
    await expect(traffic.getByTestId('traffic-p95')).toHaveText(/^(\d+ ms|[\d.]+ s|n\/a)$/);
  }

  const alarms = page.locator('[data-panel=alarms] [data-alarm]');
  await expect(alarms.first()).toBeVisible();
  for (const state of await alarms.evaluateAll(els => els.map(e => e.getAttribute('data-alarm-state')))) {
    expect(['OK', 'ALARM', 'INSUFFICIENT_DATA']).toContain(state);
  }

  const github = page.locator('[data-panel=github]');
  await expect(github.getByTestId('open-issues')).toHaveText(/^\d+$/);
  await expect(github.locator('[data-workflow]').first()).toBeVisible();
  // Open issues by the labels triage sets, and the agent's PRs still in flight.
  for (const label of await github.locator('[data-triage-label]').evaluateAll(els => els.map(e => e.getAttribute('data-label')))) {
    expect(label).toMatch(/^(agent|needs-human|risk:.+|difficulty:.+)$/);
  }
  await expect(github.getByTestId('agent-in-flight')).toHaveText(/^\d+$/);
  if (!settings.live) {
    await expect(github.locator('[data-triage-label]').first()).toBeVisible();
    await expect(github.locator('[data-testid=agent-prs] [data-pr-state=open]').first()).toBeVisible();
  }

  const flags = page.locator('[data-panel=flags] [data-flag]');
  await expect(flags.first()).toBeVisible();
  const flagName = await flags.first().getAttribute('data-flag');
  await expect(page.getByTestId(`flag-toggle-${flagName}`)).toBeVisible();
  // A flag's description says what it does; it never just repeats the name.
  for (const row of await flags.all()) {
    const name = await row.getAttribute('data-flag');
    await expect(row.locator('.muted'), `${name} description`).not.toHaveText(new RegExp(`^\\s*(${name})?\\s*$`));
  }

  // Deploys and rollbacks: the count of rollbacks, then each deploy with its environment and the short commit SHA.
  await expect(page.getByTestId('rollback-count')).toHaveAttribute('data-rollbacks', /^\d+$/);
  const deploys = page.locator('[data-panel=deploys] [data-testid=deploy-entry]');
  await expect(deploys.first()).toBeVisible();
  await expect(deploys.first()).toContainText(/\b(prod|staging)\b/);
  await expect(deploys.first().getByTestId('deploy-commit')).toHaveText(/^[0-9a-f]{7}$/);

  await expect(page.getByTestId('cost-mtd')).toHaveText(/\$\s?[\d,]+(\.\d+)?/);
  await expect(page.getByTestId('cost-today')).toHaveText(/\$\s?[\d,]+(\.\d+)?/);
});

test('Status starts any load scenario for the minutes typed in', async ({ page }) => {
  test.skip(settings.live, 'Starts real load on production.');
  await page.goto('/');
  const load = page.locator('[data-panel=load]');
  for (const scenario of ['normal', 'peak', 'spike', 'soak']) {
    await expect(load.getByTestId(`load-start-${scenario}`)).toBeVisible();
  }

  await load.getByTestId('load-minutes').fill('7');
  await load.getByTestId('load-start-peak').click();
  await expect(page.getByTestId('toast')).toContainText('peak for 7 min');
  await expect(page.locator('[data-panel=load] [data-testid=load-state]')).toHaveAttribute('data-load-running', 'true');
  await expect(page.locator('[data-panel=load] [data-testid=load-state]')).toContainText('peak');
  // What was typed survives the panel refreshing itself.
  await expect(page.getByTestId('load-minutes')).toHaveValue('7');

  await page.locator('[data-panel=load]').getByTestId('load-stop').click();
  await expect(page.locator('[data-panel=load] [data-testid=load-state]')).toHaveAttribute('data-load-running', 'false');
});
