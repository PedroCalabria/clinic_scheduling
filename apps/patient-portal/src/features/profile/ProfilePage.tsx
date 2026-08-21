import {
  Alert,
  Badge,
  Button,
  Card,
  CardDescription,
  CardHeader,
  CardTitle,
  Field,
  Input,
  getMyProfile,
  revokeConsent,
  updateMyProfile,
  useApiErrorMessage,
  type ConsentResponse,
} from '@clinic/shared';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';

const PROFILE_QUERY_KEY = ['patients', 'me'] as const;

/**
 * P7 — Profile and consents (docs/06-ui-surfaces.md).
 *
 * The screen where the ownership rule becomes visible: everything here is the signed-in
 * patient's own record, fetched from `/api/patients/me`, which is why the frontend never
 * needs to know its own patient id — and why no id it could send would widen what it sees
 * (design A8).
 *
 * Utilitarian even within the showcase portal, by design (Z2). What it does owe the user is
 * clarity about what is held and what they agreed to, which is the LGPD-awareness scope this
 * project takes on.
 */
export function ProfilePage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const describeError = useApiErrorMessage();

  const { data: profile, isPending, isError, error } = useQuery({
    queryKey: PROFILE_QUERY_KEY,
    queryFn: getMyProfile,
    retry: false,
  });

  const [fullName, setFullName] = useState('');
  const [contactPhone, setContactPhone] = useState('');
  const [saved, setSaved] = useState(false);

  // Seeded from the server once it answers. A controlled form needs local state, and this is
  // the seam where it starts — not a second source of truth for the profile itself.
  useEffect(() => {
    if (profile) {
      setFullName(profile.fullName);
      setContactPhone(profile.contactPhone ?? '');
    }
  }, [profile]);

  const save = useMutation({
    mutationFn: () => updateMyProfile(fullName, contactPhone.trim() === '' ? null : contactPhone),
    onSuccess: (updated) => {
      queryClient.setQueryData(PROFILE_QUERY_KEY, updated);
      setSaved(true);
    },
  });

  const revoke = useMutation({
    mutationFn: (type: string) => revokeConsent(type),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: PROFILE_QUERY_KEY }),
  });

  if (isPending) {
    return (
      <p role="status" className="text-meta">
        {t('common.loading')}
      </p>
    );
  }

  if (isError || !profile) {
    return <Alert tone="error">{describeError(error)}</Alert>;
  }

  return (
    <div className="space-y-6">
      <Card>
        <CardHeader>
          <CardTitle>{t('profile.title')}</CardTitle>
          <CardDescription>{t('profile.description')}</CardDescription>
        </CardHeader>

        <form
          className="space-y-5"
          onSubmit={(event) => {
            event.preventDefault();
            setSaved(false);
            save.mutate();
          }}
        >
          <Field label={t('profile.fullName')}>
            {({ id, describedBy, invalid }) => (
              <Input
                id={id}
                aria-describedby={describedBy}
                aria-invalid={invalid}
                value={fullName}
                onChange={(event) => setFullName(event.target.value)}
                required
              />
            )}
          </Field>

          {/*
            Read-only rather than absent: the patient should see which address the clinic
            has, but it comes from the identity provider and changing it here would put the
            two out of step.
          */}
          <Field label={t('profile.contactEmail')} hint={t('profile.contactEmailNote')}>
            {({ id, describedBy }) => (
              <Input id={id} aria-describedby={describedBy} value={profile.contactEmail} readOnly disabled />
            )}
          </Field>

          <Field
            label={`${t('profile.contactPhone')} (${t('common.optional')})`}
            hint={t('profile.contactPhoneNote')}
          >
            {({ id, describedBy }) => (
              <Input
                id={id}
                type="tel"
                aria-describedby={describedBy}
                value={contactPhone}
                onChange={(event) => setContactPhone(event.target.value)}
              />
            )}
          </Field>

          {save.isError ? <Alert tone="error">{describeError(save.error)}</Alert> : null}
          {saved ? <Alert tone="success">{t('common.saved')}</Alert> : null}

          <Button type="submit" disabled={save.isPending}>
            {t('common.save')}
          </Button>
        </form>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{t('profile.consents')}</CardTitle>
        </CardHeader>

        {revoke.isError ? <Alert tone="error">{describeError(revoke.error)}</Alert> : null}

        {profile.consents.length === 0 ? (
          <p className="text-meta">{t('profile.noConsents')}</p>
        ) : (
          <ul className="space-y-4">
            {profile.consents.map((consent) => (
              <ConsentRow
                key={`${consent.type}-${consent.grantedAtUtc}`}
                consent={consent}
                onRevoke={() => revoke.mutate(consent.type)}
                revoking={revoke.isPending}
              />
            ))}
          </ul>
        )}
      </Card>
    </div>
  );
}

function ConsentRow({
  consent,
  onRevoke,
  revoking,
}: {
  consent: ConsentResponse;
  onRevoke: () => void;
  revoking: boolean;
}) {
  const { t, i18n } = useTranslation();

  const formatDate = (value: string) =>
    new Date(value).toLocaleDateString(i18n.language, { dateStyle: 'long' });

  const label =
    consent.type === 'DataProcessing'
      ? t('profile.consentDataProcessing')
      : t('profile.consentCalendarSync');

  return (
    <li className="flex flex-wrap items-start justify-between gap-4 border-b border-line pb-4 last:border-0 last:pb-0">
      <div className="space-y-1">
        <div className="flex items-center gap-2">
          <span className="font-semibold text-body">{label}</span>
          {/* Text, not just colour — the state has to survive a colour-blind reader. */}
          <Badge tone={consent.active ? 'active' : 'off'}>
            {consent.active ? t('profile.consentActive') : t('profile.consentInactive')}
          </Badge>
        </div>

        <p className="text-sm text-meta">
          {t('profile.consentGranted', {
            date: formatDate(consent.grantedAtUtc),
            version: consent.version,
          })}
        </p>

        {/* The withdrawal is shown alongside the grant, never instead of it. */}
        {consent.revokedAtUtc ? (
          <p className="text-sm text-meta">
            {t('profile.consentRevoked', { date: formatDate(consent.revokedAtUtc) })}
          </p>
        ) : null}
      </div>

      {consent.active ? (
        <div className="space-y-1 text-right">
          <Button variant="secondary" size="sm" onClick={onRevoke} disabled={revoking}>
            {t('profile.revoke')}
          </Button>
          <p className="max-w-xs text-xs text-meta">{t('profile.revokeConfirm')}</p>
        </div>
      ) : null}
    </li>
  );
}
