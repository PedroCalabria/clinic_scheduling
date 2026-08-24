import { Alert, Button, getBookingOptions } from '@clinic/shared';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { useNavigate, useSearchParams } from 'react-router';
import { slotTime } from './slots';

/**
 * P4 — confirmation (docs/06-ui-surfaces.md, design B15).
 *
 * Reassurance, and nothing else. The details are carried in the URL from P3 rather than re-fetched:
 * the appointment was just created and the response described it, so a second request would only
 * add a way for this screen to fail after the thing it reports has already succeeded.
 *
 * **The onward link is temporary and says so.** "My appointments" (P5) is `booking-lifecycle`'s
 * screen, so this points at the profile for now. Named here and in the validation guide, because a
 * dangling link found during a demo is worse than one a reader was told about.
 */
export function BookingSuccessPage() {
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const [params] = useSearchParams();

  const start = params.get('start') ?? '';
  const end = params.get('end') ?? '';
  const timezone = params.get('timezone') ?? '';
  const appointmentTypeId = params.get('type') ?? '';
  const professionalId = params.get('professional') ?? '';

  // Only for the two labels. Already cached from P2 and P3, so this normally resolves instantly and
  // never blocks the confirmation itself from rendering.
  const options = useQuery({ queryKey: ['booking-options'], queryFn: getBookingOptions, retry: false });

  const appointmentType = options.data?.specialties
    .flatMap((specialty) => specialty.appointmentTypes)
    .find((type) => type.appointmentTypeId === appointmentTypeId);

  const professional = appointmentType?.professionals.find(
    (entry) => entry.professionalId === professionalId,
  );

  if (!start || !timezone) {
    return (
      <div className="space-y-4">
        <Alert tone="info">{t('booking.successMissing')}</Alert>
        <Button onClick={() => void navigate('/book')}>{t('booking.bookAnother')}</Button>
      </div>
    );
  }

  const day = new Intl.DateTimeFormat(i18n.language, {
    timeZone: timezone,
    weekday: 'long',
    day: 'numeric',
    month: 'long',
    year: 'numeric',
  }).format(new Date(start));

  return (
    <div className="space-y-8">
      <div className="rounded-xl border border-primary bg-primary-subtle p-8">
        <p className="text-sm font-medium uppercase tracking-wide text-primary-strong">
          {t('booking.successBadge')}
        </p>

        <h1 className="mt-2 text-2xl font-semibold tracking-tight text-heading">
          {t('booking.successTitle')}
        </h1>

        <dl className="mt-6 space-y-3">
          <div className="flex flex-wrap gap-x-3">
            <dt className="text-meta">{t('booking.when')}</dt>
            <dd className="font-medium text-heading">
              {day}, {slotTime(start, timezone)}–{slotTime(end, timezone)}
            </dd>
          </div>
          <div className="flex flex-wrap gap-x-3">
            <dt className="text-meta">{t('booking.appointmentType')}</dt>
            <dd className="font-medium text-heading">{appointmentType?.name ?? '—'}</dd>
          </div>
          <div className="flex flex-wrap gap-x-3">
            <dt className="text-meta">{t('booking.professional')}</dt>
            <dd className="font-medium text-heading">{professional?.displayName ?? '—'}</dd>
          </div>
        </dl>

        {/*
          Which clock, said out loud on the one screen a patient is most likely to write the time
          down from. The clinic has exactly one zone (Decision H) and it may not be the reader's.
        */}
        <p className="mt-4 text-sm text-meta">{t('booking.timezoneNote', { timezone })}</p>
      </div>

      <section className="space-y-3">
        <h2 className="font-medium text-heading">{t('booking.whatNextTitle')}</h2>
        <p className="text-meta">{t('booking.whatNextBody')}</p>
      </section>

      <div className="flex flex-wrap gap-3">
        {/*
          Points at the profile, not at "my appointments": P5 arrives with booking-lifecycle. A
          working link to a real screen beats a link to a route that does not exist.
        */}
        <Button onClick={() => void navigate('/profile')}>{t('booking.viewProfile')}</Button>
        <Button variant="secondary" onClick={() => void navigate('/book')}>
          {t('booking.bookAnother')}
        </Button>
      </div>
    </div>
  );
}
