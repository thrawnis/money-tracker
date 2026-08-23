import { useEffect, useState } from 'react';
import { getTimeZones, type TimeZoneOption } from '../api/preferences';
import { detectTimeZone } from '../utils/timezone';

interface Props {
  value: string;
  onChange: (id: string) => void;
  id?: string;
  className?: string;
  disabled?: boolean;
  tabIndex?: number;
  /** Placeholder shown while nothing is chosen. */
  placeholder?: string;
}

/**
 * Timezone picker backed by the server's list of resolvable IANA zones —
 * validation happens against that same list server-side, so offering anything
 * else here would just produce a rejected save.
 *
 * The list is fetched once per mount. If the request fails the detected
 * browser zone is still offered on its own, so a user is never stuck unable to
 * answer a question the app refuses to let them past.
 */
export default function TimeZoneSelect({
  value, onChange, id, className, disabled, tabIndex, placeholder = 'Select your time zone…',
}: Props) {
  const [zones, setZones] = useState<TimeZoneOption[]>([]);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    getTimeZones().then(setZones).catch(() => setFailed(true));
  }, []);

  const detected = detectTimeZone();
  const options = zones.length > 0
    ? zones
    : [detected, value].filter((z, i, a): z is string => !!z && a.indexOf(z) === i)
        .map(z => ({ id: z, displayName: z }));

  return (
    <>
      <select
        id={id}
        className={className}
        value={value}
        onChange={e => onChange(e.target.value)}
        disabled={disabled}
        tabIndex={tabIndex}
      >
        <option value="">{placeholder}</option>
        {options.map(tz => (
          <option key={tz.id} value={tz.id}>
            {tz.id === detected ? `${tz.id} (detected)` : tz.id}
            {tz.displayName && tz.displayName !== tz.id ? ` — ${tz.displayName}` : ''}
          </option>
        ))}
      </select>
      {failed && zones.length === 0 && (
        <div style={{ fontSize: 11, color: '#b8860b', marginTop: 4 }}>
          Couldn't load the full zone list — only your detected zone is available.
        </div>
      )}
    </>
  );
}
