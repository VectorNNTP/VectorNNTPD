using System.Collections;
using System.Data.Common;

namespace VectorNNTPD.BackFillerService.Tests.TestDoubles
{
    /// <summary>
    /// Minimal <see cref="DbDataReader"/> that surfaces real <see cref="DBNull.Value"/> cells for account-row tests.
    /// </summary>
    internal sealed class SequenceDbDataReader : DbDataReader
    {
        /// <summary>Column names in ordinal order matching each row array.</summary>
        private readonly string[] _names;

        /// <summary>Row value arrays; cells may be <see cref="DBNull.Value"/> for SQL NULL.</summary>
        private readonly object[][] _rows;

        /// <summary>Current row ordinal; <c>-1</c> before the first successful <see cref="Read"/>.</summary>
        private int _index = -1;

        /// <summary>Set by <see cref="Close"/>; subsequent reads throw.</summary>
        private bool _closed;

        /// <summary>Builds a reader over named columns and row value arrays (use <see cref="DBNull.Value"/> for SQL NULL).</summary>
        /// <param name="names">Column names in ordinal order.</param>
        /// <param name="rows">Row value arrays. Each array length must match <paramref name="names"/>.</param>
        internal SequenceDbDataReader(string[] names, params object[][] rows)
        {
            ArgumentNullException.ThrowIfNull(names);
            ArgumentNullException.ThrowIfNull(rows);
            if (names.Length == 0)
            {
                throw new ArgumentException("At least one column name is required.", nameof(names));
            }

            foreach (var row in rows)
            {
                ArgumentNullException.ThrowIfNull(row);
                if (row.Length != names.Length)
                {
                    throw new ArgumentException("Each row must match the column count.", nameof(rows));
                }
            }

            _names = names;
            _rows = rows;
        }

        /// <inheritdoc />
        public override int Depth => 0;

        /// <inheritdoc />
        public override int FieldCount => _names.Length;

        /// <inheritdoc />
        public override bool HasRows => _rows.Length > 0;

        /// <inheritdoc />
        public override bool IsClosed => _closed;

        /// <inheritdoc />
        public override int RecordsAffected => -1;

        /// <inheritdoc />
        public override object this[int ordinal] => GetValue(ordinal);

        /// <inheritdoc />
        public override object this[string name] => GetValue(GetOrdinal(name));

        /// <inheritdoc />
        public override bool GetBoolean(int ordinal) => throw new NotSupportedException();

        /// <inheritdoc />
        public override byte GetByte(int ordinal) => throw new NotSupportedException();

        /// <inheritdoc />
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
            throw new NotSupportedException();

        /// <inheritdoc />
        public override char GetChar(int ordinal) => throw new NotSupportedException();

        /// <inheritdoc />
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
            throw new NotSupportedException();

        /// <inheritdoc />
        public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;

        /// <inheritdoc />
        public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();

        /// <inheritdoc />
        public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();

        /// <inheritdoc />
        public override double GetDouble(int ordinal) => throw new NotSupportedException();

        /// <inheritdoc />
        public override IEnumerator GetEnumerator() => throw new NotSupportedException();

        /// <inheritdoc />
        public override Type GetFieldType(int ordinal) => GetValue(ordinal) is null or DBNull ? typeof(object) : GetValue(ordinal).GetType();

        /// <inheritdoc />
        public override float GetFloat(int ordinal) => throw new NotSupportedException();

        /// <inheritdoc />
        public override Guid GetGuid(int ordinal) => throw new NotSupportedException();

        /// <inheritdoc />
        public override short GetInt16(int ordinal) => throw new NotSupportedException();

        /// <inheritdoc />
        public override int GetInt32(int ordinal) => throw new NotSupportedException();

        /// <inheritdoc />
        public override long GetInt64(int ordinal) => throw new NotSupportedException();

        /// <inheritdoc />
        public override string GetName(int ordinal) => _names[ordinal];

        /// <inheritdoc />
        public override int GetOrdinal(string name)
        {
            for (var i = 0; i < _names.Length; i++)
            {
                if (string.Equals(_names[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            throw new IndexOutOfRangeException(name);
        }

        /// <inheritdoc />
        public override string GetString(int ordinal) => throw new NotSupportedException();

        /// <inheritdoc />
        public override object GetValue(int ordinal)
        {
            EnsureOpenRow();
            return CurrentRow[ordinal];
        }

        /// <inheritdoc />
        public override int GetValues(object[] values)
        {
            ArgumentNullException.ThrowIfNull(values);
            EnsureOpenRow();
            var count = Math.Min(values.Length, FieldCount);
            Array.Copy(CurrentRow, values, count);
            return count;
        }

        /// <inheritdoc />
        public override bool IsDBNull(int ordinal)
        {
            var value = GetValue(ordinal);
            return value is null or DBNull;
        }

        /// <inheritdoc />
        public override bool NextResult() => false;

        /// <inheritdoc />
        public override bool Read()
        {
            EnsureOpen();
            if (_index + 1 >= _rows.Length)
            {
                return false;
            }

            _index++;
            return true;
        }

        /// <inheritdoc />
        public override Task<bool> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read());
        }

        /// <inheritdoc />
        public override void Close() => _closed = true;

        /// <summary>Gets the row at <see cref="_index"/> or throws when no row is current.</summary>
        /// <exception cref="InvalidOperationException">No successful <see cref="Read"/> has positioned a row.</exception>
        private object[] CurrentRow
        {
            get
            {
                if (_index < 0 || _index >= _rows.Length)
                {
                    throw new InvalidOperationException("No current row.");
                }

                return _rows[_index];
            }
        }

        /// <summary>Throws when the reader was closed via <see cref="Close"/>.</summary>
        /// <exception cref="InvalidOperationException">The reader is closed.</exception>
        private void EnsureOpen()
        {
            if (_closed)
            {
                throw new InvalidOperationException("Reader is closed.");
            }
        }

        /// <summary>Ensures the reader is open and a current row exists before cell access.</summary>
        /// <exception cref="InvalidOperationException">Closed reader or no current row.</exception>
        private void EnsureOpenRow()
        {
            EnsureOpen();
            _ = CurrentRow;
        }
    }
}
