using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LockstepBase {
    public class CircularBuffer<T> {

        private T[] _buff;
        private int _curIdx;
        public bool IsEmpty { get { return _curIdx == -1; } }

        public CircularBuffer(int size) {
            _buff = new T[size];
            _curIdx = -1;
        }

        public void Add(T toAdd) {
            _curIdx = Inc(_curIdx);
            _buff[_curIdx] = toAdd;
        }

        public T GetTop() {
            return _buff[_curIdx];
        }

        public T GetNth(int n) {
            int idx = _curIdx;
            for (int i=0; i<n; i++) {
                idx = Dec(idx);
            }
            return _buff[idx];
        }

        private int Inc(int i) {
            return (i + 1) % _buff.Length;
        }

        private int Dec(int i) {
            return (i - 1) < 0 ? _buff.Length - 1 : i-1;
        }
    }
}
