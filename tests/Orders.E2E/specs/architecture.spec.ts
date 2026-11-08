import { expect, test } from '@playwright/test';

// Content/ArchitectureContent.cs: zones and their component cards.
const zones: Record<string, string[]> = {
  GitHub: ['Repository', 'Issues and pull requests', 'Actions workflows', 'Merge queue', 'Agents GitHub App', 'Dependabot'],
  AWS: [
    'ECS cluster: demo', 'Load balancer', 'ECR', 'RDS PostgreSQL', 'AppConfig', 'CloudWatch and X-Ray', 'EventBridge',
    'IAM roles', 'Route 53 and ACM', 'Terraform state and OIDC provider', 'Cost Explorer',
  ],
  'Outside services': ['Claude', 'Google sign-in', '1Password'],
};

test('Architecture shows the diagram and every component card', async ({ page }) => {
  await page.goto('/');
  await page.getByTestId('nav-architecture').click();
  await expect(page.getByTestId('nav-architecture')).toHaveAttribute('aria-current', 'page');

  const arch = page.getByTestId('architecture');
  await expect(arch).toBeVisible();
  await expect(page.getByTestId('architecture-diagram')).toBeVisible();

  await expect(arch.locator('[data-zone]')).toHaveCount(Object.keys(zones).length);
  await expect(arch.locator('[data-component]')).toHaveCount(Object.values(zones).flat().length);
  for (const [zone, components] of Object.entries(zones)) {
    const z = arch.locator(`[data-zone="${zone}"]`);
    await expect(z, zone).toBeVisible();
    for (const c of components) {
      const card = z.locator(`[data-component="${c}"]`);
      await expect(card, `${zone} › ${c}`).toBeVisible();
      await expect(card).toContainText(c);
    }
  }
});
